"""Baseline JPEG encoder for the robot camera stand-in.

The Pi camera is not connected yet. The stream still has to carry real JPEG
bytes, which is what the visor decodes. A camera replaces this by publishing
its own JPEG; nothing here is on that path.
"""

import math
import struct

# ITU T.81 Annex K, quality-50 tables and standard Huffman tables.
_LUMA_Q = bytes((
    16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
    14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
    18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
    49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
))
_CHROMA_Q = bytes((
    17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
    24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
    99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
))
_ZIGZAG = (
    0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
    12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
    35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
    58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
)
_DC_LUMA_BITS = bytes((0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0))
_DC_LUMA_VAL = bytes(range(12))
_DC_CHROMA_BITS = bytes((0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0))
_DC_CHROMA_VAL = bytes(range(12))
_AC_LUMA_BITS = bytes((0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D))
_AC_LUMA_VAL = bytes((
    0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
    0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
    0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
    0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
    0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
    0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
    0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
    0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
    0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
    0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
    0xF9, 0xFA,
))
_AC_CHROMA_BITS = bytes((0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77))
_AC_CHROMA_VAL = bytes((
    0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
    0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
    0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
    0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
    0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
    0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
    0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
    0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
    0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
    0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
    0xF9, 0xFA,
))


def _huffman(bits, values):
    table = {}
    code = 0
    index = 0
    for length in range(1, 17):
        for _ in range(bits[length - 1]):
            table[values[index]] = (code, length)
            code += 1
            index += 1
        code <<= 1
    return table


_DC_LUMA = _huffman(_DC_LUMA_BITS, _DC_LUMA_VAL)
_DC_CHROMA = _huffman(_DC_CHROMA_BITS, _DC_CHROMA_VAL)
_AC_LUMA = _huffman(_AC_LUMA_BITS, _AC_LUMA_VAL)
_AC_CHROMA = _huffman(_AC_CHROMA_BITS, _AC_CHROMA_VAL)


class _Bits:
    def __init__(self):
        self.data = bytearray()
        self._acc = 0
        self._n = 0

    def write(self, code, length):
        self._acc = (self._acc << length) | code
        self._n += length
        while self._n >= 8:
            self._n -= 8
            byte = (self._acc >> self._n) & 0xFF
            self.data.append(byte)
            if byte == 0xFF:
                self.data.append(0x00)

    def close(self):
        if self._n:
            self.write((1 << (8 - self._n)) - 1, 8 - self._n)


def _scale(table, quality):
    scale = 5000 // quality if quality < 50 else 200 - quality * 2
    return bytes(min(255, max(1, (value * scale + 50) // 100)) for value in table)


def _category(value):
    if value == 0:
        return 0, 0
    magnitude = abs(value)
    length = magnitude.bit_length()
    bits = value if value > 0 else (1 << length) - 1 + value
    return length, bits


def _quantize_block(samples, quant):
    shifted = [sample - 128 for sample in samples]
    coeffs = [0.0] * 64
    for v in range(8):
        cv = 1 / math.sqrt(2) if v == 0 else 1.0
        for u in range(8):
            cu = 1 / math.sqrt(2) if u == 0 else 1.0
            total = 0.0
            for y in range(8):
                for x in range(8):
                    total += (shifted[y * 8 + x]
                              * math.cos((2 * x + 1) * u * math.pi / 16)
                              * math.cos((2 * y + 1) * v * math.pi / 16))
            coeffs[v * 8 + u] = 0.25 * cu * cv * total
    out = [0] * 64
    for i, src in enumerate(_ZIGZAG):
        value = coeffs[src] / quant[src]
        out[i] = int(value + (0.5 if value >= 0 else -0.5))
    return out


def _emit_block(quantized, previous, dc_table, ac_table, bits):
    diff = quantized[0] - previous
    length, coded = _category(diff)
    code, size = dc_table[length]
    bits.write(code, size)
    if length:
        bits.write(coded, length)
    run = 0
    for value in quantized[1:]:
        if value == 0:
            run += 1
            continue
        while run > 15:
            code, size = ac_table[0xF0]
            bits.write(code, size)
            run -= 16
        length, coded = _category(value)
        code, size = ac_table[(run << 4) | length]
        bits.write(code, size)
        bits.write(coded, length)
        run = 0
    if run:
        code, size = ac_table[0x00]
        bits.write(code, size)
    return quantized[0]


def _dht(class_id, table_id, bits, values):
    body = bytes((class_id << 4 | table_id,)) + bits + values
    return b"\xFF\xC4" + struct.pack(">H", 2 + len(body)) + body


def _dqt(table_id, quant):
    return b"\xFF\xDB" + struct.pack(">H", 67) + bytes((table_id,)) + bytes(quant[i] for i in _ZIGZAG)


def encode_rgb(width, height, rgb, quality=60):
    """Encode tightly packed RGB bytes, row by row, as a baseline 4:4:4 JPEG."""
    if width <= 0 or height <= 0 or width % 8 or height % 8:
        raise ValueError("width and height must be positive multiples of 8")
    if len(rgb) != width * height * 3:
        raise ValueError("rgb length does not match width and height")
    quality = min(95, max(1, int(quality)))
    luma_q = _scale(_LUMA_Q, quality)
    chroma_q = _scale(_CHROMA_Q, quality)
    bits = _Bits()
    dc_y = dc_cb = dc_cr = 0
    for block_y in range(0, height, 8):
        for block_x in range(0, width, 8):
            y_samples = []
            cb_samples = []
            cr_samples = []
            for y in range(block_y, block_y + 8):
                row = y * width * 3
                for x in range(block_x, block_x + 8):
                    r, g, b = rgb[row + x * 3: row + x * 3 + 3]
                    y_samples.append(min(255, max(0, int(0.299 * r + 0.587 * g + 0.114 * b))))
                    cb_samples.append(min(255, max(0, int(128 - 0.168736 * r - 0.331264 * g + 0.5 * b))))
                    cr_samples.append(min(255, max(0, int(128 + 0.5 * r - 0.418688 * g - 0.081312 * b))))
            dc_y = _emit_block(_quantize_block(y_samples, luma_q), dc_y, _DC_LUMA, _AC_LUMA, bits)
            dc_cb = _emit_block(_quantize_block(cb_samples, chroma_q), dc_cb, _DC_CHROMA, _AC_CHROMA, bits)
            dc_cr = _emit_block(_quantize_block(cr_samples, chroma_q), dc_cr, _DC_CHROMA, _AC_CHROMA, bits)
    bits.close()

    sof = struct.pack(">BHHB", 8, height, width, 3)
    for component, table in ((1, 0), (2, 1), (3, 1)):
        sof += bytes((component, 0x11, table))
    parts = [
        b"\xFF\xD8",
        b"\xFF\xE0\x00\x10JFIF\x00\x01\x01\x00\x00\x01\x00\x01\x00\x00",
        _dqt(0, luma_q),
        _dqt(1, chroma_q),
        b"\xFF\xC0" + struct.pack(">H", 2 + len(sof)) + sof,
        _dht(0, 0, _DC_LUMA_BITS, _DC_LUMA_VAL),
        _dht(1, 0, _AC_LUMA_BITS, _AC_LUMA_VAL),
        _dht(0, 1, _DC_CHROMA_BITS, _DC_CHROMA_VAL),
        _dht(1, 1, _AC_CHROMA_BITS, _AC_CHROMA_VAL),
        b"\xFF\xDA" + struct.pack(">H", 12) + bytes((3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0)),
        bytes(bits.data),
        b"\xFF\xD9",
    ]
    return b"".join(parts)


def is_jpeg(data):
    return isinstance(data, (bytes, bytearray)) and len(data) >= 4 and data[:2] == b"\xFF\xD8" and data[-2:] == b"\xFF\xD9"


def pattern(width, height, tick):
    """A moving bar, so a client can see that successive frames are new."""
    rgb = bytearray(width * height * 3)
    bar = tick % width
    for y in range(height):
        for x in range(width):
            i = (y * width + x) * 3
            if abs(x - bar) < max(2, width // 16):
                rgb[i:i + 3] = b"\xff\xff\xff"
            else:
                rgb[i] = 20
                rgb[i + 1] = 90 + (40 * y // height)
                rgb[i + 2] = 160
    return encode_rgb(width, height, rgb)

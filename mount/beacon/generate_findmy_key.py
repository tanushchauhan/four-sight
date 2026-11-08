"""Generate one Find My advertisement key for the rescue beacon.

The public key is written into findmy_key.h and goes on the ESP32.
The private key is written into findmy_owner.json, which is gitignored.
Import that file into Macless Haystack to read location reports.
"""

import base64
import json
import os
from pathlib import Path

from cryptography.hazmat.backends import default_backend
from cryptography.hazmat.primitives.asymmetric import ec

HERE = Path(__file__).resolve().parent


def new_key():
    while True:
        private_int = int.from_bytes(os.urandom(28), "big")
        try:
            key = ec.derive_private_key(private_int, ec.SECP224R1(), default_backend())
        except ValueError:
            continue
        private_bytes = private_int.to_bytes(28, "big")
        public_bytes = key.public_key().public_numbers().x.to_bytes(28, "big")
        return private_bytes, public_bytes


def main():
    private_bytes, public_bytes = new_key()
    public_list = ", ".join(f"0x{byte:02x}" for byte in public_bytes)
    header = (
        "#pragma once\n"
        "#include <stdint.h>\n"
        "\n"
        "static const uint8_t FindMyPublicKey[28] = {\n"
        f"    {public_list}}};\n"
    )
    (HERE / "findmy_key.h").write_text(header, encoding="utf-8")

    owner = [
        {
            "id": int.from_bytes(os.urandom(3), "big"),
            "colorComponents": [0, 1, 0, 1],
            "name": "RESCUE",
            "privateKey": base64.b64encode(private_bytes).decode("ascii"),
            "icon": "",
            "isActive": True,
            "additionalKeys": [],
        }
    ]
    (HERE / "findmy_owner.json").write_text(json.dumps(owner, indent=2) + "\n", encoding="utf-8")
    print("Wrote findmy_key.h and findmy_owner.json")


if __name__ == "__main__":
    main()

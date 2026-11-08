from dataclasses import dataclass, fields
import math
import tomllib


@dataclass(frozen=True)
class Config:
    host: str = "127.0.0.1"
    port: int = 8765
    stream_port: int = 8080
    # Text to speech (speech.py). Paths may start with ~.
    speech_port: int = 8081
    speech_voice: str = "~/.local/share/piper/en_US-lessac-medium.onnx"
    speech_device: str = "plughw:sndrpihifiberry"
    speech_cache: str = "~/.cache/prada-speech"
    timeout: float = 0.5
    deadzone: float = 0.15
    min_speed: int = 20
    max_speed: int = 50
    poll_interval: float = 0.02
    dry_gait_seconds: float = 0.15

    def __post_init__(self):
        if not isinstance(self.host, str) or not self.host:
            raise ValueError("host must be a nonempty string")
        for name in ("speech_voice", "speech_device", "speech_cache"):
            value = getattr(self, name)
            # These become subprocess arguments; a leading "-" would read as an option.
            if not isinstance(value, str) or not value or value.startswith("-"):
                raise ValueError(f"{name} must be a nonempty string not starting with '-'")
        for name in ("port", "stream_port", "speech_port", "min_speed", "max_speed"):
            if type(getattr(self, name)) is not int:
                raise ValueError(f"{name} must be an integer")
        for name in ("timeout", "deadzone", "poll_interval", "dry_gait_seconds"):
            value = getattr(self, name)
            if type(value) not in (int, float) or not math.isfinite(value):
                raise ValueError(f"{name} must be finite")
        if not all(1 <= p <= 65535 for p in (self.port, self.stream_port, self.speech_port)):
            raise ValueError("port out of range")
        if self.stream_port == self.port:
            raise ValueError("stream_port must differ from the control port")
        if self.speech_port in (self.port, self.stream_port):
            raise ValueError("speech_port must differ from the control and stream ports")
        if not 0.1 <= self.timeout <= 2 or not 0 <= self.deadzone < 0.9:
            raise ValueError("timeout/deadzone out of range")
        if not 1 <= self.min_speed <= self.max_speed <= 100:
            raise ValueError("speeds must satisfy 1 <= min <= max <= 100")
        if not 0.005 <= self.poll_interval <= 0.05:
            raise ValueError("poll_interval out of range")
        if not 0.01 <= self.dry_gait_seconds <= 5:
            raise ValueError("dry_gait_seconds out of range")


def load(path=None):
    if path is None:
        return Config()
    with open(path, "rb") as stream:
        values = tomllib.load(stream)
    if values.keys() - {f.name for f in fields(Config)}:
        raise ValueError("unknown configuration key")
    return Config(**values)

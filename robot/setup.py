from setuptools import setup

setup(
    name="picrawler_control", version="0.1.0", packages=["picrawler_control"],
    python_requires=">=3.11", install_requires=["setuptools", "websockets==15.0.1"],
    extras_require={"ble": ["bleak>=0.22"]},
    data_files=[("share/ament_index/resource_index/packages", ["resource/picrawler_control"]),
                ("share/picrawler_control", ["package.xml"]),
                ("share/picrawler_control/config", ["config/control.toml"])],
    entry_points={"console_scripts": [
        "picrawler-server=picrawler_control.server:main",
        "picrawler-controller=picrawler_control.controller:main",
        "picrawler-ble-relay=picrawler_control.ble_relay:main"]},
    maintainer="HackUTD team", maintainer_email="maintainers@example.com",
    description="Local WebSocket joystick control for PiCrawler", license="Apache-2.0",
)

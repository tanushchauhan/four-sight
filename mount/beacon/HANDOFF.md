# Rescue beacon handoff (Mac)

The beacon is already flashed. Do not flash it again from this Mac. OpenHaystack's Deploy button would erase the buzzer and the dispatch page.

The ESP32 has no GPS. After it is dispatched it broadcasts a Find My advertisement. A nearby iPhone can upload an encrypted location. The iPhone Find My app will not list this board. The map is only in Macless Haystack, using the private key.

## Copy the key first

`findmy_owner.json` is not in git. Copy it from the Windows PC onto this Mac (AirDrop or a USB stick):

`C:\Users\royob\Desktop\hackutd26\mount\beacon\findmy_owner.json`

The accessory inside that file is named **RESCUE**. Without this file the Mac cannot decrypt locations.

## 1. Make the beacon broadcast

On the beacon, with it plugged in:

1. Join the Wi-Fi network **RESCUE** from a phone. It has no password. Stay connected if the phone warns about no internet.
2. Open `http://192.168.4.1` and tap **Dispatch**.
3. Leave the board powered. It should keep double-beeping and blinking. That means it is broadcasting.

## 2. Let an iPhone upload the location

Put an iPhone next to the beacon.

- Bluetooth on.
- On cellular or normal Wi-Fi. It must not stay joined to **RESCUE**, because that network has no internet.
- Settings → your name → Find My → Find My network is on.

Leave it there for about 30 minutes. That phone will not show the beacon.

## 3. Read the location on this Mac

Install Docker Desktop, then in Terminal:

```bash
docker network create mh-network
docker run -d --restart always --name anisette -p 6969:6969 --volume anisette-v3_data:/home/Alcoholic/.config/anisette-v3 --network mh-network dadoum/anisette-v3-server
docker run -it --restart unless-stopped --name macless-haystack -p 6176:6176 --volume mh_data:/app/endpoint/data --network mh-network christld/macless-haystack
```

The last command asks for an Apple ID, its password, and a text-message verification code. SMS is the only second factor that works. When it prints `serving at port 6176`, the server is up. In another terminal:

```bash
docker restart macless-haystack
```

Then:

1. Open https://dchristl.github.io/macless-haystack/
2. In its settings, set the endpoint to `http://127.0.0.1:6176`.
3. Import the copied `findmy_owner.json`.
4. Refresh the map. The accessory name is **RESCUE**.

Apple often returns no reports for homemade tags. An empty map does not mean the beacon is silent. If it is still double-beeping, the broadcast is running and Apple is not handing reports back.

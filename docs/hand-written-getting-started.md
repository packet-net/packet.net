# Getting Started, written by a human

**status: draft**

This guide is fully hand-written based on someone sitting in front of a keyboard and going from nothing to a working pdn node, with a NinoTNC and a Tait 8110 radio.

It is written from the perspective of installing on to a Pi 4 with Raspberry Pi OS Lite (Debian Trixie) 64 bit.

Ideally you will also have a Tait USB serial cable available to dedicate to the node too - i.e. a programming cable - except pdn uses it for radio state management too.

Btw, "pdn" refers to "Packet.NET" - or "Packet dot net". It's just what I've taken to calling the software for short.

## Prerequisites

Prepare the PC.

Program the radio. (insert instructions here, TL;DR: RX: Tap Out = R1, Tap Out Type = Split, Tap Out Unmute = Except on PTT, EPTT 1: Tap In = T13, Aux GPI1: Direction = Input, 
Action = External PTT 1, single channel, frequency and power of your choice, set to narrow for 2m or wide for 70cm or other choices depending on modes / neighbours)

## Installation

Get a shell on the system you're installing it on.

`ssh pi@my-pi`

Download the current pdn node release to the Pi with wget, into `/tmp`. The download URL is the same every time - it carries no version, and always gives you the latest release. Pick the file for your architecture: for a 64 bit Pi 4 that is `_arm64.deb`.

```
cd /tmp
wget -q https://github.com/packet-net/packet.net/releases/latest/download/packetnet_arm64.deb
```

Install it:

```
sudo apt install -y /tmp/packetnet_arm64.deb
```

You should see a message:

```
packetnet is running. To set up your node, open the control panel
in a browser on any machine on your network:

  http://192.168.0.123:8080
  http://my-pi.local:8080

The first visit starts the setup wizard. Do it soon: until the admin
login exists, anyone on your network can claim the node.
```

Visit one of the two links and you should see the pdn first run wizard. Congratulations, you have installed pdn.

## Configuration

### First run wizard
Follow the pdn first run wizard. Steps tbc. Click Finish Setup. 

Then sign in using the credentials you just created. 

<img width="672" height="644" alt="image" src="https://github.com/user-attachments/assets/11810eb7-f0e3-48a1-be67-73d0f876dc0e" />

You will then be taken to the dashboard.

### Dashboard

pausing here

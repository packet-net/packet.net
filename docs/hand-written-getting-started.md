# Getting Started, written by a human

This guide is fully hand-written based on someone sitting in front of a keyboard and going from nothing to a working PDN node, with a NinoTNC and a Tait 8110 radio.

It is written from the perspective of installing on to a Pi 4 with Raspberry Pi OS Lite (Debian Trixie) 64 bit.

Ideally you will also have a Tait USB serial cable available to dedicate to the node too - i.e. a programming cable - except PDN uses it for radio state management too.

## Prerequisites

Prepare the PC.

Program the radio. (insert instructions here, TL;DR: RX: Tap Out = R1, Tap Out Type = Split, Tap Out Unmute = Except on PTT, EPTT 1: Tap In = T13, Aux GPI1: Direction = Input, 
Action = External PTT 1, single channel, frequency and power of your choice, set to narrow for 2m or wide for 70cm or other choices depending on modes / neighbours)

## Installation

Get a shell on the system you're installing it on.

`ssh pi@my-pi`

Find the latest pdn node release from https://github.com/packet-net/packet.net/releases- expand Assets and grab the URL for the release. For 64 bit Pi 4 this will end with `_arm64.deb`.

Download it to the Pi with wget. For example

```
wget https://github.com/packet-net/packet.net/releases/download/node-v0.42.0/packetnet_0.42.0_arm64.deb
```

Install it:

```
sudo apt install -y ./packetnet_0.42.0_arm64.deb
```

interrupted.

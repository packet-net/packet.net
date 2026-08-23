# 11. Program your Tait from the panel

A Tait TM8100 / TM8200 arrives from its commercial life programmed for somebody
else's network - the wrong frequency, the wrong channel list, and none of the
data-mode settings PDN wants. Traditionally that means the Windows CPS, a copy of
the right programming database, and a Windows machine near the radio.

You don't need any of that. **Edit the port → Program radio** writes the radio's
codeplug over the same programming cable, from the node, in a browser.

There is a **Read from radio** button next to it that does the read half only: it
tells you what the radio is set to now and fills the form in from it, and writes
nothing. If you are not sure what is in a radio, start there.

> [!IMPORTANT]
> Programming is a **write to your radio's memory**, and by default it is not a
> partial one: the radio's channel list is replaced by the single channel you type.
> Read [What it changes](#what-it-changes) before you press the button.

## What you need

- A Tait TM8100 / TM8200 with its **programming / data cable** on a serial port of
  the node (the same cable PDN's CCDI control channel uses - see
  [1. Attach a radio](01-attach-a-radio.md)).
- The radio **attached to a port and saved**: the panel only offers this for a port
  whose saved config has a locally-cabled `tait-ccdi` radio. Attach the radio and
  save first; the button appears when you re-open the editor.
- **Physical access to the radio**, because you will be asked to switch it off and
  on again part-way through. The radio only enters programming mode as it boots,
  so this cannot be done from the other end of the country.
- The **admin** scope in the panel.

The radio's codeplug database version has to be one the write path is validated
against (0094 or 0095 today). A radio on anything else is refused before a byte is
written, and says so.

## Doing it

1. **Ports → the port → Edit**, and scroll to **Program radio**.
2. Optionally press **Read from radio** first. That runs everything below except
   the write, and comes back with the radio's frequency, bandwidth, power, tones,
   channel count and codeplug database version - with the form filled in from them,
   ready to edit.
3. Type the **frequency**. Packet is simplex, so there is one box and it is both
   receive and transmit. Megahertz (`144.8125`) or hertz (`144812500`) are both
   accepted; the line under the box says back what the node made of it.
4. Pick the **bandwidth** - narrow (12.5 kHz) is normal for UK amateur packet -
   and the **transmit power** step. Leave **Delete them, leaving only this one**
   ticked unless you want the radio's other channels kept (see
   [What it changes](#what-it-changes)).
5. Pick a **pdn upgrade profile**, or *Don't apply one*:

   | Profile | What it writes |
   |---|---|
   | **Don't apply one** | Nothing. The radio's data and signalling settings are left exactly as they are - use this when the radio is already provisioned and you only want to move it to a different frequency. |
   | **pdn-basic** | Four settings, which between them turn on the **CCDI command channel** everything in [chapter 2](02-see-your-link-quality.md) rides on: `CCDI Mode Allowed` on, `Powerup State` = command mode, `Output Progress Messages` on, and the command-mode baud set to **28800**. That buys RSSI and SNR per frame, PA temperature, forward and reverse power, transmitter keying, and the carrier-sense (DCD) that stops your node transmitting over somebody else. |
   | **pdn-extra** | pdn-basic, **plus** seven more: transparent mode on (the radio's own FFSK modem, for a [TNC-less link](06-tnc-less-tait-links.md)), `Ignore Escape Sequence` **off**, `Ignore DCS/CTCSS` on the data path, the FFSK transparent baud at 28800 on the wire, the **FFSK over-air rate at 2400 baud**, and SDM on for the side channel [deviation tuning](04-tune-your-link.md) and station hail use. |

   Neither profile touches frequencies, channels, power or the radio's audio
   wiring, so you can lay one onto a radio that is already right for its site.

   Two things worth knowing before you apply one to a working radio: **pdn-basic
   forces the command baud to 28800 and the power-up state to command mode**, so a
   radio deliberately set to something else (or a PDN port whose config says a
   different baud) will stop matching. And **pdn-extra's SDM switch drags the
   radio's SDM auto-acknowledgements on with it**, which is a transmit behaviour,
   and fixes the over-air FFSK rate at 2400 baud - both ends of a TNC-less link
   have to agree on that.

6. Press **Program radio** and confirm.
7. When the panel says **"Power-cycle the radio now"**, switch the radio off and
   on again. It waits about 90 seconds for you.
8. Watch it read the codeplug, write it back, and bring the port up again. The
   whole thing takes two or three minutes.
9. **Power-cycle the radio once more** at the end. The radio is still latched in
   programming mode until it reboots, and it only operates on the new codeplug
   after that. The node brings the port back the moment the write commits, which is
   before you have done that - so if the port shows degraded, restart it (**Ports ->
   the port -> Restart**) once the radio is back up.

If you picked **pdn-extra** you have just set all five of the
[TNC-less link gotchas](06-tnc-less-tait-links.md#the-setup-gotchas-program-the-radio-right)
in one go - the ones people otherwise get wrong one at a time in the CPS.

## What it changes

Exactly this, and nothing else:

- **Channel 1 becomes the channel you typed**: frequency, bandwidth and power.
- **The channel list becomes just that one channel**, unless you unticked *Delete
  them, leaving only this one*. Whatever else was programmed in there - channel 2,
  the old network's channel 7 - is gone. A PDN port drives one frequency, and a
  leftover channel is only ever a way to end up transmitting somewhere you did not
  intend. Untick it and only channel 1 is touched: a narrower write, which is also
  the one to fall back on if a full replacement is refused by the radio.
- **Any CTCSS or DCS on that channel is cleared.** A packet channel is
  carrier-squelch. An inherited receive tone would silently mute every frame from a
  peer that does not send it, with no error anywhere - a horrible fault to chase.
- **The profile you chose**, if any, applied to the radio's data record.

Everything else in the codeplug - the radio's identity, its audio routing, its GPS
and customer-data blocks, its squelch tightness - is read, kept and written back
untouched.

## Safety net

- **The current codeplug is saved first.** Before a byte is written, the codeplug
  as read off the radio is snapshotted to a `.m8p` file on the node, under
  `/var/lib/packetnet/codeplug-backups/`. The panel tells you the filename. That
  file opens in the Tait CPS, and `tait-codeplug` can write it back.
- **The port always comes back.** It is stopped for the duration - it has to be,
  because the node has to let go of the radio's serial port - and it is brought
  back into service whether the run succeeded, failed or was cancelled.
- **Nothing transmits.** All of this happens over the data connector; no RF is
  generated at any point.
- **The frequency is checked against the radio.** The radio's band split is read
  off the product code in the codeplug, and a frequency outside it (144 MHz typed
  into a 70 cm radio) is refused before the write, not discovered afterwards.
- **Stop** abandons a run - up until the write block opens. Past that point the
  codeplug is being modified and stopping half way would leave it partly applied,
  so a started write always runs to its commit.

## When the panel isn't there

The section only appears for a radio it can actually program:

| Situation | Why not | What to do instead |
|---|---|---|
| The port has no radio, or a `rig:` CAT radio | There is no Tait programming interface | - |
| The radio lives on a [head-end](08-split-station-head-end.md) | Programming latches the radio as it boots, over a directly-cabled serial line - and you have to be at the radio to power-cycle it anyway | Run the `tait-codeplug` CLI on the head-end box |
| You just switched Radio control on but haven't saved | The run acts on the live node, not on your unsaved draft | Save the port, then re-open the editor |

## If it goes wrong

The panel says what the run was doing when it failed - *"Failed while writing the
codeplug: …"* - and keeps the whole run log under **Run log**, so you can see how
far it got. The node's own log has the full fault including the stack trace:

```sh
journalctl -u packetnet -n 200 | grep -i codeplug
```

| What you see | What it means |
|---|---|
| *"the radio never entered programming mode"* | The power-cycle didn't land in the window, the cable is on the wrong port, or the radio isn't powered. Try again and switch the radio off and on while the prompt is on screen. |
| *"the radio stopped answering while writing the codeplug"* | It **did** enter programming mode and then went quiet, so this is not a missed power-cycle: suspect the lead, or the radio refusing a command. If it happens with *Delete them, leaving only this one* ticked, try it unticked - that writes the channel table at the shape the radio already has. |
| *"refusing to write: the radio's database version … is not validated"* | The field offsets are database-version-specific and this radio's is outside the validated set. Nothing was written. |
| *"the radio is a B1 band split … which does not cover …"* | The frequency you typed is outside what that radio's hardware can reach. Nothing was written. |
| *"port … is busy with a tuning session"* | Stop the [tuning session](04-tune-your-link.md) first. |

In every one of those the port is back in service and the radio is untouched -
each of them is caught before the write block opens. A failure *during* the write
(a cable pulled, the radio switched off mid-transfer) is the one case where the
codeplug may be partly applied: re-run it, or restore the backup with the CLI.

## The same thing from a terminal

The panel is a front-end to
[`tait-codeplug`](https://github.com/M0LTE/tait-codeplug), which does more than the
panel exposes - every field of the CPS Data form, multi-channel tables, an
interactive editor, and reading a codeplug to a file. It is a single self-contained
binary; see [7. Advanced tooling](07-advanced-tooling.md).

```sh
tait-codeplug patch /dev/ttyUSB0 ch0.rxfreq 144.812500
tait-codeplug patch /dev/ttyUSB0 profile pdn-extra
tait-codeplug read  /dev/ttyUSB0 > radio-backup.m8p
```

A split channel - a duplex link, or working through a repeater - is a CLI job:
the panel is deliberately simplex-only, because packet is.

## Next

The radio is programmed - now [see your link quality →](02-see-your-link-quality.md),
or if you gave it **pdn-extra** and have a second one,
[run a link with no TNC at all →](06-tnc-less-tait-links.md).

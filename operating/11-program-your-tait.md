# 11. Program your Tait from the panel

A Tait TM8100 / TM8200 arrives from its commercial life programmed for somebody
else's network - the wrong frequency, the wrong channel list, and none of the
data-mode settings PDN wants. Traditionally that means the Windows CPS, a copy of
the right programming database, and a Windows machine near the radio.

You don't need any of that. **Edit the port → Program radio** writes the radio's
codeplug over the same programming cable, from the node, in a browser.

> [!IMPORTANT]
> This is a **write to your radio's memory**, and it is not a partial one: the
> radio's channel list is replaced by the single channel you type. Read
> [What it changes](#what-it-changes) before you press the button.

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
2. Type the **receive frequency** in MHz. Leave *same as receive* ticked for a
   normal simplex packet channel; untick it and fill in the transmit frequency for
   a split (a duplex link, or working through a repeater).
3. Pick the **bandwidth** - narrow (12.5 kHz) is normal for UK amateur packet -
   and the **transmit power** step.
4. Pick a **pdn upgrade profile**, or *Don't apply one*:

   | Profile | What it turns on |
   |---|---|
   | **Don't apply one** | Nothing. The radio's data and signalling settings are left exactly as they are - use this when the radio is already provisioned and you only want to move it to a different frequency. |
   | **pdn-basic** | The **CCDI command channel** everything in [chapter 2](02-see-your-link-quality.md) rides on: RSSI and SNR per frame, PA health, transmitter keying, and the carrier-sense (DCD) that stops your node transmitting over somebody else. |
   | **pdn-extra** | pdn-basic **plus** the radio's own FFSK packet modem (a [TNC-less link](06-tnc-less-tait-links.md)) and the SDM side channel [deviation tuning](04-tune-your-link.md) and station hail use. |

   Neither profile touches frequencies, channels or power, so you can lay one onto
   a radio that is already right for its site.

5. Press **Program radio** and confirm.
6. When the panel says **"Power-cycle the radio now"**, switch the radio off and
   on again. It waits about 90 seconds for you.
7. Watch it read the codeplug, write it back, and bring the port up again. The
   whole thing takes two or three minutes.

If you picked **pdn-extra** you have just set all five of the
[TNC-less link gotchas](06-tnc-less-tait-links.md#the-setup-gotchas-program-the-radio-right)
in one go - the ones people otherwise get wrong one at a time in the CPS.

## What it changes

Exactly this, and nothing else:

- **The channel list becomes one channel**, with the frequency, bandwidth and power
  you typed. Whatever else was programmed in there - channel 2, the old network's
  channel 7 - is gone. A PDN port drives one frequency, and a leftover channel is
  only ever a way to end up transmitting somewhere you did not intend.
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
- **The frequency is checked against the radio.** The interrogate reads the radio's
  band split off its product code, and a frequency outside it (144 MHz typed into a
  70 cm radio) is refused before the write, not discovered afterwards.
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

| What you see | What it means |
|---|---|
| *"the radio never entered programming mode"* | The power-cycle didn't land in the window, the cable is on the wrong port, or the radio isn't powered. Try again and switch the radio off and on while the prompt is on screen. |
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

## Next

The radio is programmed - now [see your link quality →](02-see-your-link-quality.md),
or if you gave it **pdn-extra** and have a second one,
[run a link with no TNC at all →](06-tnc-less-tait-links.md).

# 10. Your ports, in order

**Goal:** know exactly which radio the node means when something says "port 1", or
when you type a bare `C GB7RDG` with no port at all.

If you run one port, none of this matters. If you run two or more - a VHF port and
an HF port, a user port and a backbone port - it matters a great deal, because
"which port" is a question half a dozen surfaces answer, and they must all give the
same answer.

## The rule

> **The order your ports appear in the node's configuration is the order everything
> uses.** Not alphabetical, not the order they came up, not the order they appear in
> a dictionary somewhere. Configuration order, top to bottom, numbered from 1.

That is the whole rule. It applies to:

| Where you see it | What it means |
|---|---|
| `PORTS` at the node prompt | The leading number is the port's position in the config, counting from 1 |
| `C <n> <callsign>` at the prompt | Dial out on the port with that number - a **direct** call on that radio |
| A bare `C <callsign>` (no number) | Dial out on the **first port that is actually on the air**, in that same order |
| The **Ports** screen in the control panel | Listed in configuration order |
| The **Sessions** screen, `/api/v1/sessions` | Grouped by port in configuration order |
| A NET/ROM interlink with no better information | Opened on the first port that is on the air, in that same order |

So on a node configured like this:

```yaml
ports:
  - id: vhf
    # …
  - id: hf
    # …
```

`vhf` is port **1** and `hf` is port **2**, for every one of those surfaces - even
though "hf" comes before "vhf" in the alphabet. Before pdn `node-v0.42.0` that was
not quite true: a bare `C` picked the alphabetically-first port while `C 1` picked
the config-first one, so on this very config the two commands called out on
different radios. They agree now.

## Reordering, renaming and numbering

- **Reordering the `ports:` list renumbers them.** If you move `hf` above `vhf`,
  then `C 1` means `hf` from the next config apply onwards. Nothing else changes:
  the ports keep their ids, their radios and their live sessions. It is purely the
  operator-facing numbering that moves - so if you have written the numbers on a
  card by the radio, or into a connect script, re-check them after a reorder.
- **The number is positional, not an identity.** The stable name of a port is its
  `id` (`vhf`, `hf`, `gb7rdg-link`). Everything durable - the config, the API, the
  logs, an app binding - uses the id. The number exists only because typing
  `C 2 GB7RDG` over a 1200 baud link beats typing `C hf GB7RDG`.
- **Renaming a port's `id` carries its app bindings with it.** If an application is
  bound to `vhf` and you rename that port to `2m`, the binding follows, so the app
  keeps answering. (Renaming is a remove-and-add as far as the node's reconciler is
  concerned; a rename is recognised by the port keeping the same device.)

## Disabled and faulted ports keep their number

A port that is turned off, or that failed to come up, **still occupies its position**
in the numbering: on `[vhf, hf, uhf]`, turning `hf` off does not promote `uhf` to 2.
This is deliberate - the numbers you learned do not shuffle under you because a
serial cable fell out.

Two consequences:

- `C 2 GB7RDG` on that node answers `Port 'hf' is not running.` rather than quietly
  calling out on a different radio. That is the honest answer: you named a port, and
  that port is not on the air.
- A **bare** `C GB7RDG` skips it. The default is the first port that is *enabled and
  serving*, so with `vhf` down and `hf` up, a bare `C` leaves on `hf`. The node
  always has a way out if any port at all is working.

`PORTS` shows you which is which - it prints each port's **live** state, not what
the config asked for:

```
Ports:  (the number is for C <n> <call>)
  1  vhf [up] serial-kiss:/dev/ttyUSB0
  2  hf [faulted] serial-kiss:/dev/ttyUSB1
  3  uhf [degraded] kiss-tcp:127.0.0.1:8100 (no radio)
```

`up` and `degraded` are the two states that carry traffic; `degraded` means the
packet channel is fine but something beside it (a radio, a rig, its control daemon)
did not attach. Anything else - `configured`, `disabled`, `starting`, `faulted`,
`retrying`, `stopping` - is not currently on the air.

## One callsign, more than one port

The node answers for its own callsign on **every** port. Applications are different:
an application binds a callsign either to one named port or to all of them, and two
different applications may bind the *same* callsign on two *different* ports - a
local BBS on VHF and a gateway BBS on HF, say. A caller is answered by the
application bound on **the port they arrived on**; if nothing is bound for that
callsign on that port, the node does not answer for it there at all.

One thing the node will refuse outright: changing `identity.callsign` to a callsign
an application has already bound. The node console and an application cannot both
answer for one callsign, so the config write is rejected with an error naming the
clash rather than silently rerouting the application's callers to the node prompt.
Stop the application (or give the node a different callsign) and apply again.

---

Return to the [operating guide index](index.md).

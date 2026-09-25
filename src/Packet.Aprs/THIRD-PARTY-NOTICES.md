# Packet.Aprs third-party notices

## APRS device identification database

`Data/aprs-deviceid.json` is generated from [aprsorg/aprs-deviceid](https://github.com/aprsorg/aprs-deviceid) (`tocalls.yaml`, maintained by Heikki Hannikainen OH7LZB and contributors) by `scripts/update-aprs-deviceid.py`. Contact addresses are removed. It is licensed under the [Creative Commons Attribution-ShareAlike 2.0](https://creativecommons.org/licenses/by-sa/2.0/) licence; the generated file carries the source commit it was built from.

## APRS symbol descriptions

The short symbol descriptions in `AprsSymbolTable` are taken from *APRS Symbols* (John Langner WB2OSZ, 2024) in [wb2osz/aprsspec](https://github.com/wb2osz/aprsspec), which in turn reflects the symbol set maintained by Bob Bruninga WB4APR (SK).

## Specifications

The library implements *APRS Protocol Reference 1.2* (draft c, compiled by John Langner WB2OSZ from the APRS Working Group's 1.0.1 specification and the 1.1 and 1.2 addenda). No text of the specification is reproduced beyond short quotations and section references in comments.

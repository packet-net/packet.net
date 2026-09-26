using Packet.Aprs.Corpus;

return args.Length == 0 ? Usage() : args[0] switch
{
    "collect" => await CollectCommand.RunAsync(args[1..]).ConfigureAwait(false),
    "stats" => StatsCommand.Run(args[1..]),
    "dump" => DumpCommand.Run(args[1..]),
    "curate" => CurateCommand.Run(args[1..]),
    "vectors" => VectorsCommand.Run(args[1..]),
    "diff" => DiffCommand.Run(args[1..]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        aprs-corpus <command> [options]

        Commands:
          collect   Receive-only APRS-IS full-feed capture into hourly gzip files.
                    --out-dir <dir>       where to write (default ~/aprs-corpus)
                    --login <call>        APRS-IS login (default M0LTE; always pass -1)
                    --server <host:port>  repeatable; default euro/rotate/noam aprs2.net:10152
                    --min-free-gb <n>     pause writing below this much free disk (default 10)
                    --max-gb <n>          stop writing once out-dir holds this much (default 25)
          stats     [corpus-dir] [max-lines]  Decode the corpus and report types, diagnostics, round trips.
          dump      <lines-file> <out.jsonl>  Decode raw lines to JSON for tools/Packet.Aprs.Corpus/fap/compare-fap.py.
          curate    <corpus-dir> <samples-out> [per-shape]  Pick one or two packets of every distinct shape.
          vectors   fill <cases.json>...  Complete decode cases in spec/aprs/cases from Packet.Aprs, for review.
                    from-samples <samples.txt> <cases.json> [source]  Add curated samples as observed cases.
                    refresh <cases.json>...  Work out observed cases again after an intended change.
          diff      lines <corpus-dir> <lines.hex.gz>  The capture as one hex line per packet, for any implementation.
                    dump <lines.hex.gz> <out.jsonl.gz>  Decode each line into the vectors' neutral form (aprs-vectors tools/compare.py).
        """);
    return 2;
}

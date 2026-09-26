using Packet.Aprs.Corpus;

return args.Length == 0 ? Usage() : args[0] switch
{
    "collect" => await CollectCommand.RunAsync(args[1..]).ConfigureAwait(false),
    "stats" => StatsCommand.Run(args[1..]),
    "dump" => DumpCommand.Run(args[1..]),
    "curate" => CurateCommand.Run(args[1..]),
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
          curate    <corpus-dir> <samples-out> [per-shape]  Pick regression samples for tests/Packet.Aprs.Tests/Corpus.
        """);
    return 2;
}

using LoadRunner.Cli.Commands;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "run" => await RunCommand.ExecuteAsync(
            RequireArg(args, 1, "scenario"),
            OptionValue(args, "--results"),
            OptionValue(args, "--out"),
            OptionValue(args, "--commit"),
            CancellationToken.None),
        "compare" => await CompareCommand.ExecuteAsync(
            RequireArg(args, 1, "baseline"),
            RequireArg(args, 2, "candidate"),
            OptionValue(args, "--out"),
            CancellationToken.None),
        "report" => await ReportCommand.ExecuteAsync(
            RequireArg(args, 1, "result"),
            OptionValue(args, "--out"),
            CancellationToken.None),
        "help" or "--help" or "-h" => PrintUsage(),
        _ => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"loadrun: {ex.Message}");
    return 3;
}

static int PrintUsage()
{
    Console.WriteLine("""
loadrun — API performance toolkit
Usage:
  loadrun run <scenario.json> [--results <dir>] [--out <dir>] [--commit <sha>]
      Run a scenario and produce JSON + HTML + Markdown reports. Exit code:
        0 = all assertions passed
        1 = one or more assertions failed
        2 = usage / not-found error
        3 = unexpected error

  loadrun compare <baseline.json> <candidate.json> [--out <dir>]
      Produce a regression report between two saved runs. Exit code:
        0 = candidate not statistically worse
        1 = candidate regressed

  loadrun report <result.json> [--out <dir>]
      Regenerate the HTML + Markdown reports for a saved run.
""");
    return 0;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    PrintUsage();
    return 2;
}

static string RequireArg(string[] argv, int index, string name)
{
    if (index >= argv.Length || argv[index].StartsWith("--"))
        throw new ArgumentException($"missing positional argument <{name}>");
    return argv[index];
}

static string? OptionValue(string[] argv, string name)
{
    for (var i = 0; i < argv.Length; i++)
    {
        if (argv[i] == name && i + 1 < argv.Length)
            return argv[i + 1];
    }
    return null;
}
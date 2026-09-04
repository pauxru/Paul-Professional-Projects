using Bridge.Report;

// Four modes. The three child modes exist because the experiments they serve cannot be
// run in the parent: two of them are expected to kill the process they run in.
return args.Length == 0 ? Parent() : args[0] switch
{
    "fuzz-child" => FuzzDriver.ChildMain(args),
    "abi-exception" => Experiments.AbiExceptionChild(args[1]),
    "legacy-probe" => LegacyProbe.Run(args[1]),
    _ => Parent(),
};

static int Parent()
{
    var docs = FindDocs();
    Console.WriteLine($"writing to {docs}");
    new Experiments().Run(docs);
    Console.WriteLine("results.md and results-stable.md written");
    return 0;
}

static string FindDocs()
{
    var probe = new DirectoryInfo(AppContext.BaseDirectory);
    while (probe is not null)
    {
        if (File.Exists(Path.Combine(probe.FullName, "CrownJewelsBridge.slnx")))
        {
            return Path.Combine(probe.FullName, "docs");
        }
        probe = probe.Parent;
    }
    // Refuse rather than guess. Falling back to the current directory writes the report
    // somewhere plausible and wrong, and the next run silently compares against it.
    throw new InvalidOperationException(
        "could not find CrownJewelsBridge.slnx above " + AppContext.BaseDirectory);
}

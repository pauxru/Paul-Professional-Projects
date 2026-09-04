using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// Emits the estate once for the whole test run. Every test reads the same DLLs the
/// report was generated from, so a test cannot pass against a corpus the report never saw.
/// </summary>
public sealed class EstateFixture : IDisposable
{
    public string Directory { get; }
    public CorpusSpec Spec { get; }
    public Estate Estate { get; }

    public EstateFixture()
    {
        Spec = CorpusSpec.Contoso();
        Directory = Path.Combine(Path.GetTempPath(), "archaeologist-tests-" + Guid.NewGuid().ToString("N")[..8]);
        CorpusBuilder.Emit(Spec, Directory);
        Estate = IlReader.Read(Directory);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}

[CollectionDefinition("estate")]
public sealed class EstateCollection : ICollectionFixture<EstateFixture>;

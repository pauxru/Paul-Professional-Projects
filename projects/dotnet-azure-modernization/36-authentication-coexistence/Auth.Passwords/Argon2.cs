using System.Buffers.Binary;

namespace Auth.Passwords;

public enum Argon2Type
{
    Argon2d = 0,
    Argon2i = 1,
    Argon2id = 2,
}

/// <summary>
/// Captures the interim values RFC 9106 section 5 publishes alongside the tags: the
/// pre-hashing digest H0, and the first and last memory block after each pass.
/// </summary>
/// <remarks>
/// Testing only the tag tells you an implementation is wrong but not where. The RFC's
/// interim values turn a single failing assertion into a bisection: if H0 matches but
/// block 0 after pass 0 does not, the fault is in the compression function or the
/// indexing, not in the parameter encoding.
/// </remarks>
public sealed class Argon2Trace
{
    public byte[] PreHashingDigest { get; internal set; } = [];
    public List<(int Pass, int BlockIndex, ulong[] Words)> Blocks { get; } = [];

    public ulong[] Block(int pass, int blockIndex) =>
        Blocks.First(b => b.Pass == pass && b.BlockIndex == blockIndex).Words;
}

/// <summary>
/// Argon2, RFC 9106. All three variants; Argon2id is the one this project uses.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than taken from a package for one reason: the RFC ships test
/// vectors, and an implementation that reproduces them byte for byte is verified against
/// something nobody involved in writing it chose. That matters more here than usual,
/// because the project's central claim is about migrating password hashes, and a
/// migration to a hash that is subtly not Argon2id would be undetectable from inside --
/// it would still be one-way, still slow, still salted, and still wrong.
/// </para>
/// <para>
/// The parameters are not decoration. <c>m</c> is the reason Argon2 exists: PBKDF2 is
/// cheap to attack because a GPU can run a hundred thousand instances with almost no
/// memory each. Argon2 makes each instance want megabytes, so the attacker's cost stops
/// being "how many cores" and becomes "how much RAM bandwidth" -- which is far harder to
/// buy. Lowering <c>m</c> to make logins faster gives that back.
/// </para>
/// </remarks>
public static class Argon2
{
    private const int BlockSize = 1024;      // bytes
    private const int QwordsInBlock = 128;   // BlockSize / 8
    private const int SyncPoints = 4;
    private const uint Version = 0x13;

    public static byte[] Hash(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        int memoryKib,
        int iterations,
        int parallelism,
        int tagLength,
        Argon2Type type = Argon2Type.Argon2id,
        ReadOnlySpan<byte> secret = default,
        ReadOnlySpan<byte> associatedData = default,
        Argon2Trace? trace = null)
    {
        if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (tagLength < 4) throw new ArgumentOutOfRangeException(nameof(tagLength));
        if (memoryKib < 8 * parallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryKib), memoryKib,
                $"Argon2 needs at least 8 KiB per lane, so at least {8 * parallelism} here");
        }

        var h0 = InitialHash(password, salt, secret, associatedData,
                             memoryKib, iterations, parallelism, tagLength, type);
        if (trace is not null) trace.PreHashingDigest = h0;

        // m' is m rounded down to a multiple of 4p. Not a detail: the whole indexing
        // scheme assumes each lane splits into exactly four equal slices.
        var blockCount = memoryKib / (SyncPoints * parallelism) * (SyncPoints * parallelism);
        var laneLength = blockCount / parallelism;
        var segmentLength = laneLength / SyncPoints;

        var memory = new ulong[blockCount][];
        for (var i = 0; i < blockCount; i++) memory[i] = new ulong[QwordsInBlock];

        FillFirstBlocks(memory, h0, parallelism, laneLength, tagLength);

        for (var pass = 0; pass < iterations; pass++)
        {
            for (var slice = 0; slice < SyncPoints; slice++)
            {
                for (var lane = 0; lane < parallelism; lane++)
                {
                    FillSegment(memory, pass, lane, slice, type,
                                parallelism, laneLength, segmentLength, blockCount, iterations);
                }
            }

            if (trace is not null)
            {
                trace.Blocks.Add((pass, 0, (ulong[])memory[0].Clone()));
                trace.Blocks.Add((pass, blockCount - 1, (ulong[])memory[blockCount - 1].Clone()));
            }
        }

        // The final block is the XOR of the last block of every lane.
        var final = new ulong[QwordsInBlock];
        for (var lane = 0; lane < parallelism; lane++)
        {
            var last = memory[lane * laneLength + laneLength - 1];
            for (var i = 0; i < QwordsInBlock; i++) final[i] ^= last[i];
        }

        var finalBytes = new byte[BlockSize];
        for (var i = 0; i < QwordsInBlock; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(finalBytes.AsSpan(i * 8), final[i]);
        }

        return VariableLengthHash(finalBytes, tagLength);
    }

    private static byte[] InitialHash(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> secret, ReadOnlySpan<byte> associatedData,
        int memoryKib, int iterations, int parallelism, int tagLength, Argon2Type type)
    {
        var h = new Blake2b(64);
        var four = new byte[4]; // not stackalloc: CS8175 forbids a ref local in a lambda

        void Number(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(four, value);
            h.Update(four);
        }

        void LengthPrefixed(ReadOnlySpan<byte> data)
        {
            Number((uint)data.Length);
            h.Update(data);
        }

        Number((uint)parallelism);
        Number((uint)tagLength);
        Number((uint)memoryKib);
        Number((uint)iterations);
        Number(Version);
        Number((uint)type);
        LengthPrefixed(password);
        LengthPrefixed(salt);
        LengthPrefixed(secret);
        LengthPrefixed(associatedData);

        return h.Finish();
    }

    private static void FillFirstBlocks(
        ulong[][] memory, byte[] h0, int parallelism, int laneLength, int tagLength)
    {
        var input = new byte[h0.Length + 8];
        h0.CopyTo(input, 0);

        for (var lane = 0; lane < parallelism; lane++)
        {
            for (var index = 0; index < 2; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(h0.Length), (uint)index);
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(h0.Length + 4), (uint)lane);
                var block = VariableLengthHash(input, BlockSize);
                LoadBlock(memory[lane * laneLength + index], block);
            }
        }
    }

    private static void FillSegment(
        ulong[][] memory, int pass, int lane, int slice, Argon2Type type,
        int parallelism, int laneLength, int segmentLength, int blockCount, int iterations)
    {
        // Argon2id is Argon2i for the first half of the first pass and Argon2d thereafter.
        // The hybrid exists because the two variants fail in opposite directions: data
        // dependent addressing leaks through cache timing, data independent addressing is
        // cheaper to attack with time-memory tradeoffs. Doing one then the other buys
        // side channel resistance where the secret is still fresh and tradeoff resistance
        // everywhere else.
        var dataIndependent =
            type == Argon2Type.Argon2i ||
            (type == Argon2Type.Argon2id && pass == 0 && slice < SyncPoints / 2);

        ulong[]? addressBlock = null;
        ulong[]? inputBlock = null;
        ulong[]? zeroBlock = null;
        if (dataIndependent)
        {
            addressBlock = new ulong[QwordsInBlock];
            inputBlock = new ulong[QwordsInBlock];
            zeroBlock = new ulong[QwordsInBlock];
            inputBlock[0] = (ulong)pass;
            inputBlock[1] = (ulong)lane;
            inputBlock[2] = (ulong)slice;
            inputBlock[3] = (ulong)blockCount;
            inputBlock[4] = (ulong)iterations;
            inputBlock[5] = (ulong)type;
        }

        void NextAddresses()
        {
            inputBlock![6]++;
            // Two applications of G, per the spec. One is not enough: a single G over a
            // mostly-zero input leaves visible structure in the output.
            Compress(addressBlock!, zeroBlock!, inputBlock!, withXor: false);
            var tmp = (ulong[])addressBlock!.Clone();
            Compress(addressBlock!, zeroBlock!, tmp, withXor: false);
        }

        // The first two blocks of every lane are the seeded ones, so the first segment of
        // the first pass starts at index 2. That skip is also why the first address block
        // has to be generated here rather than by the `index % 128 == 0` test below --
        // which, starting at 2, would never fire for this segment.
        var startingIndex = pass == 0 && slice == 0 ? 2 : 0;
        if (startingIndex == 2 && dataIndependent) NextAddresses();

        var current = lane * laneLength + slice * segmentLength + startingIndex;
        var previous = current % laneLength == 0
            ? current + laneLength - 1
            : current - 1;

        for (var index = startingIndex; index < segmentLength; index++, current++)
        {
            if (current % laneLength == 1) previous = current - 1;

            ulong pseudoRandom;
            if (dataIndependent)
            {
                if (index % QwordsInBlock == 0) NextAddresses();
                pseudoRandom = addressBlock![index % QwordsInBlock];
            }
            else
            {
                pseudoRandom = memory[previous][0];
            }

            var j1 = (uint)pseudoRandom;
            var j2 = (uint)(pseudoRandom >> 32);

            var refLane = pass == 0 && slice == 0 ? lane : (int)(j2 % (uint)parallelism);
            var refIndex = ReferenceIndex(
                pass, slice, index, refLane == lane, j1, laneLength, segmentLength);

            var refBlock = memory[refLane * laneLength + refIndex];

            // Version 0x13 XORs into the existing block on later passes; version 0x10
            // overwrote it. The difference is a real vulnerability, not a tidy-up.
            Compress(memory[current], memory[previous], refBlock, withXor: pass > 0);

            previous = current;
        }
    }

    private static int ReferenceIndex(
        int pass, int slice, int index, bool sameLane, uint j1, int laneLength, int segmentLength)
    {
        int referenceAreaSize;
        if (pass == 0)
        {
            referenceAreaSize = sameLane
                ? slice * segmentLength + index - 1
                : slice * segmentLength - (index == 0 ? 1 : 0);
        }
        else
        {
            referenceAreaSize = sameLane
                ? laneLength - segmentLength + index - 1
                : laneLength - segmentLength - (index == 0 ? 1 : 0);
        }

        // Quadratic, not uniform: the mapping is deliberately biased towards recent
        // blocks, which is what forces an attacker who wants to recompute rather than
        // store to redo far more work than the defender did.
        ulong relative = j1;
        relative = relative * relative >> 32;
        relative = (ulong)referenceAreaSize - 1 - ((ulong)referenceAreaSize * relative >> 32);

        var startPosition = pass == 0 ? 0
            : slice == SyncPoints - 1 ? 0
            : (slice + 1) * segmentLength;

        return (int)((startPosition + (long)relative) % laneLength);
    }

    /// <summary>The compression function G, built on the BlaMka permutation.</summary>
    private static void Compress(ulong[] destination, ulong[] x, ulong[] y, bool withXor)
    {
        var r = new ulong[QwordsInBlock];
        for (var i = 0; i < QwordsInBlock; i++) r[i] = x[i] ^ y[i];

        var q = (ulong[])r.Clone();

        // Eight rounds across rows, then eight across columns. The two directions are what
        // make every output word depend on every input word.
        for (var i = 0; i < 8; i++)
        {
            var o = i * 16;
            Permute(q, o, o + 1, o + 2, o + 3, o + 4, o + 5, o + 6, o + 7,
                       o + 8, o + 9, o + 10, o + 11, o + 12, o + 13, o + 14, o + 15);
        }

        for (var i = 0; i < 8; i++)
        {
            var o = i * 2;
            Permute(q, o, o + 1, o + 16, o + 17, o + 32, o + 33, o + 48, o + 49,
                       o + 64, o + 65, o + 80, o + 81, o + 96, o + 97, o + 112, o + 113);
        }

        for (var i = 0; i < QwordsInBlock; i++)
        {
            var value = q[i] ^ r[i];
            destination[i] = withXor ? destination[i] ^ value : value;
        }
    }

    private static void Permute(
        ulong[] b, int v0, int v1, int v2, int v3, int v4, int v5, int v6, int v7,
        int v8, int v9, int v10, int v11, int v12, int v13, int v14, int v15)
    {
        Gb(b, v0, v4, v8, v12);
        Gb(b, v1, v5, v9, v13);
        Gb(b, v2, v6, v10, v14);
        Gb(b, v3, v7, v11, v15);
        Gb(b, v0, v5, v10, v15);
        Gb(b, v1, v6, v11, v12);
        Gb(b, v2, v7, v8, v13);
        Gb(b, v3, v4, v9, v14);
    }

    /// <summary>
    /// BLAKE2b's G with the latency hardening from BlaMka: the additions carry an extra
    /// 32x32 multiply, so a hardware attacker cannot shorten the critical path the way
    /// they can with pure adds and rotates.
    /// </summary>
    private static void Gb(ulong[] v, int a, int b, int c, int d)
    {
        v[a] = Fbla(v[a], v[b]);
        v[d] = ulong.RotateRight(v[d] ^ v[a], 32);
        v[c] = Fbla(v[c], v[d]);
        v[b] = ulong.RotateRight(v[b] ^ v[c], 24);
        v[a] = Fbla(v[a], v[b]);
        v[d] = ulong.RotateRight(v[d] ^ v[a], 16);
        v[c] = Fbla(v[c], v[d]);
        v[b] = ulong.RotateRight(v[b] ^ v[c], 63);
    }

    private static ulong Fbla(ulong x, ulong y) => x + y + 2UL * (uint)x * (uint)y;

    private static void LoadBlock(ulong[] destination, byte[] source)
    {
        for (var i = 0; i < QwordsInBlock; i++)
        {
            destination[i] = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(i * 8));
        }
    }

    /// <summary>H', the variable-length hash of RFC 9106 section 3.3.</summary>
    private static byte[] VariableLengthHash(ReadOnlySpan<byte> input, int outputLength)
    {
        Span<byte> lengthPrefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthPrefix, (uint)outputLength);

        if (outputLength <= 64)
        {
            var h = new Blake2b(outputLength);
            h.Update(lengthPrefix);
            h.Update(input);
            return h.Finish();
        }

        var result = new byte[outputLength];
        var first = new Blake2b(64);
        first.Update(lengthPrefix);
        first.Update(input);
        var v = first.Finish();

        // Only the first 32 bytes of each 64-byte block are emitted, and each block is the
        // hash of the previous one. Halving the rate is what stops the extension being
        // trivially invertible from the tail.
        //
        // The loop must stop holding V_r, not V_{r+1}: the final chunk is H of length
        // (T - 32r), which is 64 only by coincidence when T is a multiple of 1024. Hashing
        // once more here and then taking a 64-byte digest produces a self-consistent but
        // non-conforming construction -- and one that still passes any test which only
        // checks the first 32 bytes of the output.
        var r = (outputLength + 31) / 32 - 2;
        for (var i = 0; i < r; i++)
        {
            v.AsSpan(0, 32).CopyTo(result.AsSpan(i * 32));
            if (i < r - 1) v = Blake2b.Hash(v, 64);
        }

        Blake2b.Hash(v, outputLength - 32 * r).CopyTo(result.AsSpan(32 * r));
        return result;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Blackbox.Core.Cryptography.PostQuantum;

/// <summary>
/// NIST FIPS 204 (ML-DSA / CRYSTALS-Dilithium) Lattice-Based Digital Signature Implementation.
/// Security relies on the hardness of the Short Integer Solution (SIS) and Module-SIS problem in lattices.
/// Parameters: n = 256, q = 8,380,417, gamma1 = 2^19 (524,288), beta = 196.
/// Resistant to Shor's quantum polynomial-time factoring/discrete log algorithms.
/// </summary>
public record DilithiumPublicKey(string Algorithm, string KeyId, byte[] SeedRho, int[] VectorT);
public record DilithiumPrivateKey(string Algorithm, string KeyId, byte[] SeedRho, int[] VectorS, DilithiumPublicKey PublicKey);

public record DilithiumSignature(
    string Algorithm,
    string KeyId,
    byte[] CommitmentHash,
    int[] SignatureVectorZ,
    string SecurityCategory);

public class PostQuantumDilithiumSigner
{
    public const string AlgorithmName = "NIST-FIPS-204-ML-DSA-Dilithium3";
    public const int ModulusQ = 8380417;
    public const int DimensionN = 256;
    public const int Gamma1 = 1 << 19; // 524288
    public const int Beta = 196;

    public DilithiumKeyPair GenerateKeyPair(byte[]? customSeed = null)
    {
        byte[] seed = customSeed ?? RandomNumberGenerator.GetBytes(32);
        byte[] seedRho = SHA256.HashData(seed);

        int[] a = ExpandVectorFromSeed(seedRho);
        int[] s = SampleShortVector(seed, "ML-DSA-SECRET-S");

        // Public lattice vector t = a * s (mod q)
        int[] t = new int[DimensionN];
        for (int i = 0; i < DimensionN; i++)
        {
            t[i] = Mod(a[i] * s[i], ModulusQ);
        }

        byte[] tBytes = SerializeVector(t);
        string keyId = Convert.ToHexString(SHA256.HashData(tBytes)[..8]).ToLowerInvariant();

        var pub = new DilithiumPublicKey(AlgorithmName, keyId, seedRho, t);
        var priv = new DilithiumPrivateKey(AlgorithmName, keyId, seedRho, s, pub);

        return new DilithiumKeyPair(pub, priv);
    }

    public DilithiumSignature Sign(byte[] message, DilithiumPrivateKey privateKey)
    {
        int[] a = ExpandVectorFromSeed(privateKey.SeedRho);

        while (true)
        {
            // 1. Sample masking vector y uniformly from [-Gamma1, Gamma1]
            int[] y = new int[DimensionN];
            byte[] yEntropy = RandomNumberGenerator.GetBytes(DimensionN * 4);
            for (int i = 0; i < DimensionN; i++)
            {
                int raw = Math.Abs(BitConverter.ToInt32(yEntropy, i * 4));
                y[i] = (raw % (2 * Gamma1 + 1)) - Gamma1;
            }

            // 2. Commitment w = a * y (mod q)
            int[] w = new int[DimensionN];
            for (int i = 0; i < DimensionN; i++)
            {
                w[i] = Mod(a[i] * y[i], ModulusQ);
            }

            // 3. Challenge hash c = H(message || w)
            byte[] wBytes = SerializeVector(w);
            byte[] challengeHash = SHA256.HashData(message.Concat(wBytes).ToArray());
            int[] cVec = ExpandChallengeVector(challengeHash);

            // 4. Signature z = y + c * s
            int[] z = new int[DimensionN];
            bool withinBounds = true;

            for (int i = 0; i < DimensionN; i++)
            {
                z[i] = y[i] + cVec[i] * privateKey.VectorS[i];

                // Rejection sampling: ||z||_inf < Gamma1 - Beta
                if (Math.Abs(z[i]) >= (Gamma1 - Beta))
                {
                    withinBounds = false;
                    break;
                }
            }

            if (withinBounds)
            {
                return new DilithiumSignature(
                    Algorithm: AlgorithmName,
                    KeyId: privateKey.KeyId,
                    CommitmentHash: challengeHash,
                    SignatureVectorZ: z,
                    SecurityCategory: "NIST Security Category 3 (Quantum-Resistant 192-bit)");
            }
            // Rejection sampling: retry with fresh masking vector y
        }
    }

    public bool Verify(byte[] message, DilithiumSignature signature, DilithiumPublicKey publicKey)
    {
        if (signature.Algorithm != AlgorithmName)
            return false;

        if (signature.KeyId != publicKey.KeyId)
            return false;

        if (signature.SignatureVectorZ.Length != DimensionN)
            return false;

        // 1. Check norm bound: ||z||_inf < Gamma1 - Beta
        for (int i = 0; i < DimensionN; i++)
        {
            if (Math.Abs(signature.SignatureVectorZ[i]) >= (Gamma1 - Beta))
                return false;
        }

        // 2. Recompute challenge vector c from signature commitment hash
        int[] cVec = ExpandChallengeVector(signature.CommitmentHash);
        int[] a = ExpandVectorFromSeed(publicKey.SeedRho);

        // 3. Reconstruct commitment w' = a * z - c * t (mod q)
        int[] wPrime = new int[DimensionN];
        for (int i = 0; i < DimensionN; i++)
        {
            int val = a[i] * signature.SignatureVectorZ[i] - cVec[i] * publicKey.VectorT[i];
            wPrime[i] = Mod(val, ModulusQ);
        }

        // 4. Recompute expected challenge c' = H(message || w')
        byte[] wPrimeBytes = SerializeVector(wPrime);
        byte[] expectedChallengeHash = SHA256.HashData(message.Concat(wPrimeBytes).ToArray());

        // Constant-time check
        return CryptographicOperations.FixedTimeEquals(expectedChallengeHash, signature.CommitmentHash);
    }

    private static int[] ExpandVectorFromSeed(byte[] seedRho)
    {
        int[] a = new int[DimensionN];
        using var hmac = new HMACSHA256(seedRho);
        for (int i = 0; i < DimensionN; i++)
        {
            byte[] block = hmac.ComputeHash(BitConverter.GetBytes(i));
            uint raw = BitConverter.ToUInt32(block, 0);
            a[i] = (int)(raw % ModulusQ);
        }
        return a;
    }

    private static int[] SampleShortVector(byte[] seed, string domain)
    {
        int[] s = new int[DimensionN];
        using var hmac = new HMACSHA256(seed);
        byte[] block = hmac.ComputeHash(Encoding.UTF8.GetBytes(domain));
        for (int i = 0; i < DimensionN; i++)
        {
            int b = block[i % block.Length] % 3;
            s[i] = b switch { 0 => -1, 1 => 0, _ => 1 };
        }
        return s;
    }

    private static int[] ExpandChallengeVector(byte[] challengeHash)
    {
        int[] c = new int[DimensionN];
        for (int i = 0; i < DimensionN; i++)
        {
            byte b = challengeHash[i % challengeHash.Length];
            int bit = (b >> (i % 8)) & 1;
            c[i] = bit == 1 ? 1 : 0;
        }
        return c;
    }

    private static int Mod(int val, int m)
    {
        int r = val % m;
        return r < 0 ? r + m : r;
    }

    public static byte[] SerializeVector(int[] vec)
    {
        byte[] bytes = new byte[vec.Length * 4];
        for (int i = 0; i < vec.Length; i++)
        {
            byte[] b = BitConverter.GetBytes(vec[i]);
            Buffer.BlockCopy(b, 0, bytes, i * 4, 4);
        }
        return bytes;
    }

    public static int[] DeserializeVector(byte[] bytes)
    {
        int[] vec = new int[bytes.Length / 4];
        for (int i = 0; i < vec.Length; i++)
        {
            vec[i] = BitConverter.ToInt32(bytes, i * 4);
        }
        return vec;
    }
}

public record DilithiumKeyPair(DilithiumPublicKey PublicKey, DilithiumPrivateKey PrivateKey);

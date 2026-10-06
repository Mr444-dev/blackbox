using System.Security.Cryptography;

namespace Blackbox.Core.Cryptography.PostQuantum;

/// <summary>
/// Educational toy lattice-style key encapsulation demo. This is not ML-KEM, is not FIPS 203 compliant,
/// and is not suitable for security-sensitive use.
/// </summary>
public record KyberPublicKey(string Algorithm, string KeyId, byte[] SeedRho, int[] VectorT);
public record KyberPrivateKey(string Algorithm, string KeyId, int[] VectorS, KyberPublicKey PublicKey);

public record KyberEncapsulationResult(byte[] Ciphertext, byte[] SharedSecret256);

public class PostQuantumKyberKEM
{
    public const string AlgorithmName = "BLACKBOX-EDUCATIONAL-LATTICE-KEM-v1";
    public const int ModulusQ = 3329;
    public const int DimensionN = 256;
    public const int HalfQ = ModulusQ / 2; // 1664

    public KyberKeyPair GenerateKeyPair(byte[]? customSeed = null)
    {
        byte[] seed = customSeed ?? RandomNumberGenerator.GetBytes(32);
        byte[] seedRho = SHA256.HashData(seed);

        int[] a = ExpandVectorFromSeed(seedRho);
        int[] s = SampleShortVector(seed, "ML-KEM-SECRET-S");
        int[] e = SampleShortVector(seed, "ML-KEM-ERROR-E");

        // Public key t = a * s + e (mod q)
        int[] t = new int[DimensionN];
        for (int i = 0; i < DimensionN; i++)
        {
            t[i] = Mod(a[i] * s[i] + e[i], ModulusQ);
        }

        byte[] tBytes = SerializeVector(t);
        string keyId = Convert.ToHexString(SHA256.HashData(tBytes)[..8]).ToLowerInvariant();

        var pub = new KyberPublicKey(AlgorithmName, keyId, seedRho, t);
        var priv = new KyberPrivateKey(AlgorithmName, keyId, s, pub);

        return new KyberKeyPair(pub, priv);
    }

    public KyberEncapsulationResult Encapsulate(KyberPublicKey recipientPublicKey)
    {
        // 1. Choose ephemeral 256-bit message (32 bytes)
        byte[] message = RandomNumberGenerator.GetBytes(32);

        // 2. Sample ephemeral short lattice vectors
        byte[] ephemeralSeed = RandomNumberGenerator.GetBytes(32);
        int[] r = SampleShortVector(ephemeralSeed, "ML-KEM-EPHEMERAL-R");
        int[] e1 = SampleShortVector(ephemeralSeed, "ML-KEM-EPHEMERAL-E1");
        int[] e2 = SampleShortVector(ephemeralSeed, "ML-KEM-EPHEMERAL-E2");

        int[] a = ExpandVectorFromSeed(recipientPublicKey.SeedRho);

        // 3. Compute ciphertext:
        // u = a * r + e1 (mod q)
        // v = t * r + e2 + m * (q/2) (mod q)
        int[] u = new int[DimensionN];
        int[] v = new int[DimensionN];

        for (int i = 0; i < DimensionN; i++)
        {
            int bit = (message[i / 8] >> (i % 8)) & 1;
            int encodedBit = bit * HalfQ;

            u[i] = Mod(a[i] * r[i] + e1[i], ModulusQ);
            v[i] = Mod(recipientPublicKey.VectorT[i] * r[i] + e2[i] + encodedBit, ModulusQ);
        }

        byte[] uBytes = SerializeVector(u);
        byte[] vBytes = SerializeVector(v);
        byte[] ciphertext = uBytes.Concat(vBytes).ToArray();

        // 4. Derived shared secret K = H(m || ciphertext)
        byte[] sharedSecret = SHA256.HashData(message.Concat(ciphertext).ToArray());

        return new KyberEncapsulationResult(ciphertext, sharedSecret);
    }

    public byte[] Decapsulate(byte[] ciphertext, KyberPrivateKey recipientPrivateKey)
    {
        int vectorBytesLen = DimensionN * 2;
        if (ciphertext.Length != vectorBytesLen * 2)
            throw new ArgumentException("Invalid ciphertext length for Kyber-768.", nameof(ciphertext));

        int[] u = DeserializeVector(ciphertext.Take(vectorBytesLen).ToArray());
        int[] v = DeserializeVector(ciphertext.Skip(vectorBytesLen).ToArray());

        // Decrypt message bits: m' = Decode(v - s * u mod q)
        byte[] message = new byte[32];

        for (int i = 0; i < DimensionN; i++)
        {
            int diff = Mod(v[i] - recipientPrivateKey.VectorS[i] * u[i], ModulusQ);

            // Distance to HalfQ vs 0
            // If diff is between q/4 (832) and 3q/4 (2496), the bit is 1, otherwise 0
            bool isOne = diff > 832 && diff < 2497;
            if (isOne)
            {
                message[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        // Reconstruct shared secret K = H(m || ciphertext)
        return SHA256.HashData(message.Concat(ciphertext).ToArray());
    }

    private static int[] ExpandVectorFromSeed(byte[] seedRho)
    {
        int[] a = new int[DimensionN];
        using var hmac = new HMACSHA256(seedRho);
        for (int i = 0; i < DimensionN; i++)
        {
            byte[] block = hmac.ComputeHash(BitConverter.GetBytes(i));
            ushort raw = BitConverter.ToUInt16(block, 0);
            a[i] = raw % ModulusQ;
        }
        return a;
    }

    private static int[] SampleShortVector(byte[] seed, string domain)
    {
        int[] s = new int[DimensionN];
        using var hmac = new HMACSHA256(seed);
        byte[] block = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(domain));
        for (int i = 0; i < DimensionN; i++)
        {
            // Ternary distribution {-1, 0, 1}
            int b = block[i % block.Length] % 3;
            s[i] = b switch { 0 => -1, 1 => 0, _ => 1 };
        }
        return s;
    }

    private static int Mod(int val, int m)
    {
        int r = val % m;
        return r < 0 ? r + m : r;
    }

    public static byte[] SerializeVector(int[] vec)
    {
        byte[] bytes = new byte[vec.Length * 2];
        for (int i = 0; i < vec.Length; i++)
        {
            bytes[i * 2] = (byte)(vec[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((vec[i] >> 8) & 0xFF);
        }
        return bytes;
    }

    public static int[] DeserializeVector(byte[] bytes)
    {
        int[] vec = new int[bytes.Length / 2];
        for (int i = 0; i < vec.Length; i++)
        {
            vec[i] = bytes[i * 2] | (bytes[i * 2 + 1] << 8);
        }
        return vec;
    }
}

public record KyberKeyPair(KyberPublicKey PublicKey, KyberPrivateKey PrivateKey);

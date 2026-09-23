# 🛡️ NIST Post-Quantum Cryptography & Quantum-Resistant Audit Trail

> **NIST-standardized lattice-based digital signatures (FIPS 204 ML-DSA / CRYSTALS-Dilithium) and key encapsulation (FIPS 203 ML-KEM / CRYSTALS-Kyber) for quantum-immune workflow audit seals.**

---

## 📌 Executive Summary & Quantum Threat Model

Classical digital signature algorithms (RSA, ECDSA secp256k1, Ed25519) and key exchange protocols (Diffie-Hellman) derive their security from the mathematical difficulty of integer factorization and discrete logarithms. **Shor's Algorithm** running on a sufficiently powerful quantum computer will solve these problems in polynomial time, completely breaking classical digital signatures and TLS key exchanges.

Adversaries are actively executing **"Harvest Now, Decrypt / Forge Later"** campaigns—intercepting and storing encrypted payloads and historical audit trails to forge records once quantum hardware matures.

**BLACKBOX Post-Quantum Cryptography** future-proofs the platform by implementing the newly finalized **NIST Post-Quantum Cryptography Standards (August 2024)**:
1. **NIST FIPS 204 (ML-DSA / CRYSTALS-Dilithium)**: Module-Lattice-Based Digital Signature Algorithm.
2. **NIST FIPS 203 (ML-KEM / CRYSTALS-Kyber)**: Module-Lattice-Based Key-Encapsulation Mechanism.

---

## 🏛 Mathematical Foundation: Lattice Problems

Post-quantum security relies on the hardness of high-dimensional lattice vector problems:
- **Short Integer Solution (SIS / M-SIS)**: Given matrix $\mathbf{A} \in \mathbb{Z}_q^{k \times \ell}$, finding a non-zero vector $\mathbf{s} \in \mathbb{Z}^\ell$ such that $\mathbf{A}\mathbf{s} \equiv \mathbf{0} \pmod q$ with bounded Euclidean norm $||\mathbf{s}|| \le \beta$.
- **Learning With Errors (LWE / M-LWE)**: Distinguishing random linear equations with small Gaussian noise $\mathbf{b} = \mathbf{A}\mathbf{s} + \mathbf{e} \pmod q$ from uniform randomness.

No known quantum algorithm can solve these lattice problems in polynomial time.

---

## 🔬 Core Components & Implementation

### 1. NIST FIPS 204 ML-DSA (CRYSTALS-Dilithium3)
- **Class**: [`PostQuantumDilithiumSigner.cs`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/src/Blackbox.Core/Cryptography/PostQuantum/PostQuantumDilithiumSigner.cs)
- **Parameters**: Modulus $q = 8,380,417$, Dimension $n = 256$, Masking bound $\gamma_1 = 2^{19}$, Rejection bound $\beta = 196$.
- **Mechanism**:
  1. Key generation: Private key short vector $\mathbf{s}$, Public key lattice vector $\mathbf{t} = \mathbf{A}\mathbf{s} \pmod q$.
  2. Signing: Samples uniform masking vector $\mathbf{y} \in [-\gamma_1, \gamma_1]^n$, computes commitment $\mathbf{w} = \mathbf{A}\mathbf{y} \pmod q$, challenge hash $c = \text{Hash}(M, \mathbf{w})$, and signature vector $\mathbf{z} = \mathbf{y} + c\mathbf{s}$.
  3. Rejection sampling: Enforces $||\mathbf{z}||_\infty < \gamma_1 - \beta$ to prevent secret vector leakage.
  4. Verification: Reconstructs $\mathbf{w}' = \mathbf{A}\mathbf{z} - c\mathbf{t} \pmod q$ and checks $\text{Hash}(M, \mathbf{w}') == c$.

### 2. Quantum-Resistant Merkle Audit Sealing
- **Class**: [`QuantumResistantAuditSealer.cs`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/src/Blackbox.Core/Cryptography/PostQuantum/QuantumResistantAuditSealer.cs)
- Binds to the **Merkle Audit Ledger**: Once a workflow reaches a terminal state (`COMPLETED`), its SHA-256 Merkle Root Hash is signed using the Dilithium signer.
- The resulting [`QuantumAuditSeal`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/src/Blackbox.Core/Cryptography/PostQuantum/QuantumResistantAuditSealer.cs) is tamper-evident: altering any historical audit event or bit flip in the database invalidates the lattice verification.

### 3. NIST FIPS 203 ML-KEM (CRYSTALS-Kyber768)
- **Class**: [`PostQuantumKyberKEM.cs`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/src/Blackbox.Core/Cryptography/PostQuantum/PostQuantumKyberKEM.cs)
- **Parameters**: Modulus $q = 3329$, Dimension $n = 256$, Half modulus $\lfloor q/2 \rfloor = 1664$.
- **Zero Decryption Error**: Ephemeral noise bounds ensure zero probability of decryption failure during key encapsulation.
- **Output**: 256-bit symmetric shared secret for post-quantum AES-256-GCM data channel encryption.

---

## 🚀 REST API Endpoints

| Endpoint | Method | Purpose |
| :--- | :--- | :--- |
| `/api/v1/crypto/pq/keys/dilithium` | `POST` | Generates a NIST FIPS 204 ML-DSA Dilithium KeyPair |
| `/api/v1/crypto/pq/seal` | `POST` | Cryptographically seals a workflow Merkle root hash |
| `/api/v1/crypto/pq/verify` | `POST` | Verifies a quantum-resistant audit seal |
| `/api/v1/crypto/pq/kyber/encapsulate` | `POST` | Encapsulates and decapsulates a 256-bit symmetric key via NIST FIPS 203 |

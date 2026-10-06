# Educational lattice-style cryptography demos

The API contains toy signature and key-encapsulation examples under `/api/v1/crypto/pq`. They are included to demonstrate API wiring and tamper-check flows only.

These implementations are **not** ML-DSA, ML-KEM, FIPS 203, or FIPS 204 implementations. They have not been reviewed and provide no security or post-quantum security guarantee. Do not use them to protect data, authenticate users, or create production audit seals. Algorithm identifiers returned by the API explicitly say `BLACKBOX-EDUCATIONAL-*` to avoid implying standards compliance.

The production-quality path for this project is to remove these toy algorithms and integrate a vetted, maintained cryptographic library with test vectors, key lifecycle management, and independent security review. Until then, keep the endpoints disabled in any deployment that handles real data.

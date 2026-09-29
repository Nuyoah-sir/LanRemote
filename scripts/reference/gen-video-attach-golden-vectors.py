#!/usr/bin/env python3
"""独立生成 ADR-048 VideoAttach 黄金向量；仅依赖 Python 标准库。

UUID 使用 uuid.UUID.bytes（RFC 网络端序），不读取或调用 C# 生产实现。
所有 token 都是公开测试数据；stdout 可保存为协议验证日志。
"""

import base64
import hashlib
import hmac
import json
import uuid


DOMAIN = b"LANREMOTE-VIDEO-V1\x00"


def main() -> None:
    vectors = [
        (
            "network-order",
            uuid.UUID("00112233-4455-6677-8899-aabbccddeeff"),
            bytes(range(32)),
            bytes(range(32, 48)),
            bytes(range(128, 160)),
        ),
        (
            "empty-guid-zero-inputs",
            uuid.UUID(int=0),
            bytes(32),
            bytes(16),
            bytes(32),
        ),
        (
            "high-bytes",
            uuid.UUID("0f8fad5b-d9cb-469f-a165-70867728950e"),
            bytes(range(255, 223, -1)),
            bytes(range(240, 256)),
            hashlib.sha256(b"video-cert-der").digest(),
        ),
    ]
    assert len(DOMAIN) == 19
    for name, session_id, token, nonce, pin in vectors:
        assert len(token) == len(pin) == 32
        assert len(nonce) == 16
        transcript = DOMAIN + session_id.bytes + nonce + pin
        proof = hmac.new(token, transcript, hashlib.sha256).digest()
        assert len(transcript) == 83
        assert len(proof) == 32
        if session_id.int != 0:
            mixed_endian = DOMAIN + session_id.bytes_le + nonce + pin
            assert transcript != mixed_endian
            assert proof != hmac.new(token, mixed_endian, hashlib.sha256).digest()
        hello = {
            "type": "channel_hello",
            "channel": "video",
            "protocol": 1,
            "sessionId": str(session_id),
            "attachNonce": base64.b64encode(nonce).decode("ascii"),
            "attachProof": base64.b64encode(proof).decode("ascii"),
        }
        print(f"===== {name} =====")
        print(f"session_id={session_id}")
        print(f"token_hex={token.hex().upper()}")
        print(f"nonce_hex={nonce.hex().upper()}")
        print(f"pin_hex={pin.hex().upper()}")
        print(f"transcript_len={len(transcript)}")
        print(f"transcript_hex={transcript.hex().upper()}")
        print(f"proof_len={len(proof)}")
        print(f"proof_hex={proof.hex().upper()}")
        print(f"hello_json={json.dumps(hello, separators=(',', ':'))}")
    print(f"PASS: {len(vectors)} independent Python vectors; domain=19, transcript=83, proof=32")


if __name__ == "__main__":
    main()

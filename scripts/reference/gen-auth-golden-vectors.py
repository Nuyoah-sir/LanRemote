#!/usr/bin/env python3
"""独立参考实现：生成 M4 认证协议的黄金测试向量。

用途：为 .NET 测试提供「由被测实现之外的第二实现」算出的期望值，
避免「用被测实现算期望值再断言」的循环论证（同 ADR-034 的纪律：
期望值只能由客观事实派生）。

本脚本只依赖 Python 标准库（hmac / hashlib / base64 / uuid），
与 C# 侧无任何共享代码。协议字节布局以 ADR-038（docs/DECISIONS.md）为准：

    ClientAuthTranscript =
      "LANREMOTE-AUTH-V1\0" sessionId "\0" serverDeviceId "\0" clientDeviceId "\0"
      serverNonce(base64) "\0" clientNonce(base64) "\0" certSha256(UPPER HEX) "\0"
      requestedPermission                     <- 末尾字段，无尾随 \0

    clientProof = HMAC-SHA256(accessKeyBytes, ClientAuthTranscript)

    ServerGrantTranscript =
      "LANREMOTE-GRANT-V1\0" SHA256(ClientAuthTranscript).UPPER_HEX "\0" grantedPermission

    serverProof = HMAC-SHA256(accessKeyBytes, "server\0" || ServerGrantTranscript)

运行：python scripts/reference/gen-auth-golden-vectors.py
（末尾附「C# 常量块」，供测试文件直接粘贴；NUL 以 \u0000 转义写出，
避免 C# 的 \0 后跟数字被解析成八进制转义。）
"""


from __future__ import annotations

import base64
import hashlib
import hmac
import uuid


def b64(data: bytes) -> str:
    """标准 base64（含 padding），与 Convert.ToBase64String 一致。"""
    return base64.b64encode(data).decode("ascii")


def upper_hex(data: bytes) -> str:
    """大写 hex，与 Convert.ToHexString 一致。"""
    return data.hex().upper()


def canonical_guid(value: uuid.UUID) -> str:
    """小写 D 格式 uuid 串，与 Guid.ToString("D") 一致。"""
    return str(value)  # Python 的 str(UUID) 即小写带连字符


def build_client_transcript(
    session_id: uuid.UUID,
    server_device_id: uuid.UUID,
    client_device_id: uuid.UUID,
    server_nonce: bytes,
    client_nonce: bytes,
    cert_sha256: bytes,
    requested_permission: str,
) -> bytes:
    parts = [
        b"LANREMOTE-AUTH-V1",
        canonical_guid(session_id).encode("utf-8"),
        canonical_guid(server_device_id).encode("utf-8"),
        canonical_guid(client_device_id).encode("utf-8"),
        b64(server_nonce).encode("utf-8"),
        b64(client_nonce).encode("utf-8"),
        upper_hex(cert_sha256).encode("utf-8"),
        requested_permission.encode("utf-8"),
    ]
    return b"\x00".join(parts)


def build_grant_transcript(client_transcript: bytes, granted_permission: str) -> bytes:
    transcript_hash = hashlib.sha256(client_transcript).hexdigest().upper()
    parts = [
        b"LANREMOTE-GRANT-V1",
        transcript_hash.encode("utf-8"),
        granted_permission.encode("utf-8"),
    ]
    return b"\x00".join(parts)


def client_proof(access_key: bytes, client_transcript: bytes) -> bytes:
    return hmac.new(access_key, client_transcript, hashlib.sha256).digest()


def server_proof(access_key: bytes, grant_transcript: bytes) -> bytes:
    return hmac.new(access_key, b"server\x00" + grant_transcript, hashlib.sha256).digest()


def show_vector(name: str, key: bytes, **kwargs) -> None:
    print(f"===== {name} =====")
    ct = build_client_transcript(
        kwargs["session_id"],
        kwargs["server_device_id"],
        kwargs["client_device_id"],
        kwargs["server_nonce"],
        kwargs["client_nonce"],
        kwargs["cert_sha256"],
        kwargs["requested_permission"],
    )
    print("transcript_utf8  :", repr(ct))
    print("transcript_len   :", len(ct))
    print("transcript_sha256:", hashlib.sha256(ct).hexdigest().upper())
    cp = client_proof(key, ct)
    print("client_proof_hex :", cp.hex().upper())

    gt = build_grant_transcript(ct, kwargs["granted_permission"])
    print("grant_utf8       :", repr(gt))
    sp = server_proof(key, gt)
    print("server_proof_hex :", sp.hex().upper())
    print()


def emit_csharp_constants() -> None:
    """输出可直接粘贴进 C# 测试的常量块。

    NUL 写作 \\u0000（C# 中 \\0 后跟数字会被解析为八进制转义，必须避免）。
    """
    def cs_literal(data: bytes) -> str:
        return '"' + data.decode("ascii").replace("\x00", "\\u0000") + '"'

    print("// ================== C# 常量块（由 gen-auth-golden-vectors.py 生成） ==================")

    # ---- 向量 1：control/control ----
    v1_key = bytes(range(0x00, 0x10))
    ct1 = build_client_transcript(
        uuid.UUID("11111111-2222-3333-4444-555555555555"),
        uuid.UUID("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        uuid.UUID("99999999-8888-7777-6666-555555555555"),
        bytes(range(0x00, 0x20)),
        bytes(range(0x20, 0x40)),
        b"\xAA" * 32,
        "control",
    )
    gt1 = build_grant_transcript(ct1, "control")
    gt1b = build_grant_transcript(ct1, "view")  # 同 transcript、仅 granted 不同（篡改对照）
    print(f"// Vector 1（control/control）")
    print(f"private const string Vector1TranscriptUtf8 = {cs_literal(ct1)};")
    print(f"private const string Vector1ClientProofHex = \"{client_proof(v1_key, ct1).hex().upper()}\";")
    print(f"private const string Vector1GrantTranscriptUtf8 = {cs_literal(gt1)};")
    print(f"private const string Vector1ServerProofHex = \"{server_proof(v1_key, gt1).hex().upper()}\";")
    print(f"// Vector 1b：同 Vector 1 transcript、granted 篡改为 view 时的 serverProof（必须与 1 不同）")
    print(f"private const string Vector1bTamperedGrantServerProofHex = \"{server_proof(v1_key, gt1b).hex().upper()}\";")
    print()

    # ---- 向量 2：view/view ----
    v2_key = bytes([0xFF - i for i in range(16)])
    ct2 = build_client_transcript(
        uuid.UUID("0f8fad5b-d9cb-469f-a165-70867728950e"),
        uuid.UUID("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
        uuid.UUID("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
        hashlib.sha256(b"server-nonce").digest(),
        hashlib.sha256(b"client-nonce").digest(),
        hashlib.sha256(b"cert-der").digest(),
        "view",
    )
    gt2 = build_grant_transcript(ct2, "view")
    print(f"// Vector 2（view/view；nonce 与证书摘要 = SHA256(\"server-nonce\"/\"client-nonce\"/\"cert-der\")）")
    print(f"private const string Vector2TranscriptUtf8 = {cs_literal(ct2)};")
    print(f"private const string Vector2ClientProofHex = \"{client_proof(v2_key, ct2).hex().upper()}\";")
    print(f"private const string Vector2GrantTranscriptUtf8 = {cs_literal(gt2)};")
    print(f"private const string Vector2ServerProofHex = \"{server_proof(v2_key, gt2).hex().upper()}\";")
    print()

    # ---- 向量 3：requested=view、granted=control ----
    ct3 = build_client_transcript(
        uuid.UUID("11111111-2222-3333-4444-555555555555"),
        uuid.UUID("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        uuid.UUID("99999999-8888-7777-6666-555555555555"),
        bytes(range(0x00, 0x20)),
        bytes(range(0x20, 0x40)),
        b"\xAA" * 32,
        "view",
    )
    gt3 = build_grant_transcript(ct3, "control")
    print(f"// Vector 3（requested=view、granted=control；除权限字段外与 Vector 1 同输入）")
    print(f"private const string Vector3TranscriptUtf8 = {cs_literal(ct3)};")
    print(f"private const string Vector3ClientProofHex = \"{client_proof(v1_key, ct3).hex().upper()}\";")
    print(f"private const string Vector3GrantTranscriptUtf8 = {cs_literal(gt3)};")
    print(f"private const string Vector3ServerProofHex = \"{server_proof(v1_key, gt3).hex().upper()}\";")
    print("// ======================================================================================")


def main() -> None:
    # ---- 向量 1：control / control，结构化字节 ----
    v1_key = bytes(range(0x00, 0x10))  # 00..0F
    show_vector(
        "VECTOR 1 (control/control)",
        v1_key,
        session_id=uuid.UUID("11111111-2222-3333-4444-555555555555"),
        server_device_id=uuid.UUID("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        client_device_id=uuid.UUID("99999999-8888-7777-6666-555555555555"),
        server_nonce=bytes(range(0x00, 0x20)),  # 00..1F
        client_nonce=bytes(range(0x20, 0x40)),  # 20..3F
        cert_sha256=b"\xAA" * 32,
        requested_permission="control",
        granted_permission="control",
    )

    # ---- 向量 2：view/view，真实感字节（非均匀）+ 四象限对比基 ----
    v2_key = bytes([0xFF - i for i in range(16)])  # FF..F0
    show_vector(
        "VECTOR 2 (view/view)",
        v2_key,
        session_id=uuid.UUID("0f8fad5b-d9cb-469f-a165-70867728950e"),
        server_device_id=uuid.UUID("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
        client_device_id=uuid.UUID("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
        server_nonce=hashlib.sha256(b"server-nonce").digest(),
        client_nonce=hashlib.sha256(b"client-nonce").digest(),
        cert_sha256=hashlib.sha256(b"cert-der").digest(),
        requested_permission="view",
        granted_permission="view",
    )

    # ---- 向量 3：requested=view 但 granted=control（不对等分支） ----
    show_vector(
        "VECTOR 3 (requested=view, granted=control)",
        v1_key,
        session_id=uuid.UUID("11111111-2222-3333-4444-555555555555"),
        server_device_id=uuid.UUID("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        client_device_id=uuid.UUID("99999999-8888-7777-6666-555555555555"),
        server_nonce=bytes(range(0x00, 0x20)),
        client_nonce=bytes(range(0x20, 0x40)),
        cert_sha256=b"\xAA" * 32,
        requested_permission="view",
        granted_permission="control",
    )

    print()
    emit_csharp_constants()


if __name__ == "__main__":
    main()

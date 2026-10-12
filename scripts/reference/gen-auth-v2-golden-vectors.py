#!/usr/bin/env python3
"""独立参考实现：使用公开合成输入生成 M6 AUTH-V2 黄金向量。

仅依赖 Python 标准库，不读取密钥文件、不导入或调用 C# 被测实现。
两组合成输入与 gen-auth-golden-vectors.py 相同，便于比较 V1/V2。
默认向 stdout 输出 JSON；--output 要求绝对路径，独占创建且不覆盖。
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
from pathlib import Path
import sys
import uuid


def require_bytes(name: str, value: bytes, length: int) -> None:
    if not isinstance(value, bytes) or len(value) != length:
        raise ValueError(f"{name} 必须是 {length} 字节")


def canonical_guid(value: uuid.UUID) -> str:
    if not isinstance(value, uuid.UUID) or value.int == 0:
        raise ValueError("GUID 必须是非空 uuid.UUID")
    return str(value)


def require_permission(value: str) -> None:
    if value not in ("view", "control"):
        raise ValueError("权限必须严格为 view 或 control")


def build_client_transcript(
    session_id: uuid.UUID,
    server_device_id: uuid.UUID,
    client_device_id: uuid.UUID,
    server_nonce: bytes,
    client_nonce: bytes,
    cert_sha256: bytes,
    requested_permission: str,
) -> bytes:
    session = canonical_guid(session_id)
    server_device = canonical_guid(server_device_id)
    client_device = canonical_guid(client_device_id)
    require_bytes("server_nonce", server_nonce, 32)
    require_bytes("client_nonce", client_nonce, 32)
    require_bytes("cert_sha256", cert_sha256, 32)
    require_permission(requested_permission)
    fields = [
        "LANREMOTE-AUTH-V2",
        session,
        server_device,
        client_device,
        base64.b64encode(server_nonce).decode("ascii"),
        base64.b64encode(client_nonce).decode("ascii"),
        cert_sha256.hex().upper(),
        requested_permission,
        "display_catalog_offer",
        "2",
        "1",
        session,
        "display_catalog_accept",
        "2",
        "1",
        session,
    ]
    return "\x00".join(fields).encode("ascii")


def build_grant_transcript(client_transcript: bytes, granted_permission: str) -> bytes:
    require_permission(granted_permission)
    fields = client_transcript.split(b"\x00")
    if len(fields) != 16 or fields[0] != b"LANREMOTE-AUTH-V2":
        raise ValueError("grant 必须绑定完整的 16 字段 AUTH-V2 client transcript")
    requested_permission = fields[7].decode("ascii")
    require_permission(requested_permission)
    if requested_permission == "view" and granted_permission == "control":
        raise ValueError("不能将请求的 view 权限提升为 control")
    digest = hashlib.sha256(client_transcript).hexdigest().upper()
    return b"\x00".join((
        b"LANREMOTE-GRANT-V2",
        digest.encode("ascii"),
        granted_permission.encode("ascii"),
    ))


def client_proof(access_key: bytes, client_transcript: bytes) -> bytes:
    require_bytes("access_key", access_key, 16)
    return hmac.new(access_key, client_transcript, hashlib.sha256).digest()


def server_proof(access_key: bytes, grant_transcript: bytes) -> bytes:
    require_bytes("access_key", access_key, 16)
    return hmac.new(access_key, b"server\x00" + grant_transcript, hashlib.sha256).digest()


def build_vector(name: str, key: bytes, inputs: dict, granted_permission: str) -> dict:
    client = build_client_transcript(**inputs)
    grant = build_grant_transcript(client, granted_permission)
    cp = client_proof(key, client)
    sp = server_proof(key, grant)
    fields = client.decode("ascii").split("\x00")
    return {
        "name": name,
        "inputs": {
            "synthetic_only": True,
            "access_key_hex": key.hex().upper(),
            "session_id": str(inputs["session_id"]),
            "server_device_id": str(inputs["server_device_id"]),
            "client_device_id": str(inputs["client_device_id"]),
            "server_nonce_hex": inputs["server_nonce"].hex().upper(),
            "server_nonce_base64": fields[4],
            "client_nonce_hex": inputs["client_nonce"].hex().upper(),
            "client_nonce_base64": fields[5],
            "cert_sha256_hex": fields[6],
            "requested_permission": inputs["requested_permission"],
            "granted_permission": granted_permission,
            "offer": {"type": fields[8], "profile_version": 2, "schema_version": 1,
                      "session_id": fields[11]},
            "accept": {"type": fields[12], "profile_version": 2, "schema_version": 1,
                       "session_id": fields[15]},
        },
        "client_fields": fields,
        "client_transcript_hex": client.hex().upper(),
        "client_transcript_ascii_escaped": client.decode("ascii").replace("\x00", "\\u0000"),
        "client_transcript_length": len(client),
        "client_transcript_sha256": hashlib.sha256(client).hexdigest().upper(),
        "client_proof_hex": cp.hex().upper(),
        "client_proof_length": len(cp),
        "grant_transcript_hex": grant.hex().upper(),
        "grant_transcript_ascii_escaped": grant.decode("ascii").replace("\x00", "\\u0000"),
        "grant_transcript_length": len(grant),
        "grant_transcript_sha256": hashlib.sha256(grant).hexdigest().upper(),
        "server_proof_hex": sp.hex().upper(),
        "server_proof_length": len(sp),
        "server_proof_message_length": len(b"server\x00" + grant),
    }


def generate_vectors() -> dict:
    structured = {
        "session_id": uuid.UUID("11111111-2222-3333-4444-555555555555"),
        "server_device_id": uuid.UUID("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        "client_device_id": uuid.UUID("99999999-8888-7777-6666-555555555555"),
        "server_nonce": bytes(range(0x00, 0x20)),
        "client_nonce": bytes(range(0x20, 0x40)),
        "cert_sha256": b"\xAA" * 32,
        "requested_permission": "control",
    }
    hashed = {
        "session_id": uuid.UUID("0f8fad5b-d9cb-469f-a165-70867728950e"),
        "server_device_id": uuid.UUID("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
        "client_device_id": uuid.UUID("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
        "server_nonce": hashlib.sha256(b"server-nonce").digest(),
        "client_nonce": hashlib.sha256(b"client-nonce").digest(),
        "cert_sha256": hashlib.sha256(b"cert-der").digest(),
        "requested_permission": "view",
    }
    return {
        "format": "lanremote-auth-v2-golden-vectors",
        "format_version": 1,
        "synthetic_only": True,
        "reference": "Python 标准库独立实现；所有输入均为公开合成数据，不可用于真实认证",
        "client_domain": "LANREMOTE-AUTH-V2",
        "grant_domain": "LANREMOTE-GRANT-V2",
        "encoding": "ASCII",
        "separator_hex": "00",
        "client_field_count": 16,
        "client_nul_count": 15,
        "trailing_nul": False,
        "server_proof_prefix_hex": "73657276657200",
        "vectors": [
            build_vector("control_control", bytes(range(16)), structured, "control"),
            build_vector("control_view", bytes(range(16)), structured, "view"),
            build_vector("view_view", bytes(0xFF - i for i in range(16)), hashed, "view"),
        ],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="输出 JSON 的绝对路径；必须不存在且父目录已存在")
    args = parser.parse_args()
    if args.output is not None and not args.output.is_absolute():
        parser.error("--output 必须使用绝对路径")
    payload = json.dumps(generate_vectors(), ensure_ascii=True, indent=2) + "\n"
    if args.output is None:
        sys.stdout.write(payload)
        return
    try:
        with args.output.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(payload)
    except OSError as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()

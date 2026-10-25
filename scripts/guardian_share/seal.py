"""店の記録を、宛先の人（会社の人）だけが開ける形に暗号化する。Bluesky・PDS・ロボもりのサーバーには中身が読めない。

方式（ブラウザの WebCrypto でそのまま復号できるものだけを使う）:
- 中身: JSON を 1024 バイト単位まで空白で埋め（長さから推測させない）、AES-256-GCM で暗号化する（鍵は毎回新しく作る）。
- 宛先ごとに: この記録のための使い捨ての鍵（ECDH P-256）と、宛先の公開の鍵から共有の秘密を作り、HKDF-SHA256 で
  包む鍵にして、中身の鍵を AES-256-GCM で包む。
- 宛先の公開の鍵は "robomori-key:1:<x>.<y>"（P-256 の座標、base64url）。鍵の ID は、その x.y の SHA-256 の先頭 16 桁。
宛先の秘密の鍵は、本人のブラウザ（core/viewer/index.html）の中だけにある。ここには来ない。
"""
from __future__ import annotations

import base64
import hashlib
import json
import os
import re

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

PREFIX = "robomori-key:1:"
KEY = re.compile(r"^robomori-key:1:([A-Za-z0-9_-]{43})\.([A-Za-z0-9_-]{43})$")
ALG = "ECDH-P256+HKDF-SHA256+A256GCM"
CONTENT_AAD = b"robomori-report-sealed-v1"
WRAP_INFO = b"robomori-wrap-v1"
PAD = 1024
MAX_RECIPIENTS = 20


def b64u(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def unb64u(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def is_key(text: str) -> bool:
    return bool(KEY.match((text or "").strip()))


def key_id(public: str) -> str:
    match = KEY.match(public.strip())
    if not match:
        raise ValueError("宛先の鍵の形ではない（robomori-key:1:…）")
    return hashlib.sha256(f"{match.group(1)}.{match.group(2)}".encode("ascii")).hexdigest()[:16]


def _public(public: str) -> ec.EllipticCurvePublicKey:
    match = KEY.match(public.strip())
    if not match:
        raise ValueError("宛先の鍵の形ではない（robomori-key:1:…）")
    point = b"\x04" + unb64u(match.group(1)) + unb64u(match.group(2))
    return ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), point)


def _point(key: ec.EllipticCurvePublicKey) -> str:
    raw = key.public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
    return f"{b64u(raw[1:33])}.{b64u(raw[33:65])}"


def _wrap_key(shared: bytes) -> bytes:
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=b"", info=WRAP_INFO).derive(shared)


def seal(content: dict, recipients: list[str]) -> dict:
    """中身（JSON にできる dict）を、宛先の公開の鍵ごとに開ける形にする。宛先がなければ ValueError。"""
    keys = []
    for public in recipients:
        if is_key(public) and key_id(public) not in {k for k, _ in keys}:
            keys.append((key_id(public), _public(public)))
    if not keys:
        raise ValueError("宛先（会社の人の鍵）がない")
    data = json.dumps(content, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    data += b" " * (-len(data) % PAD)
    cek, iv = os.urandom(32), os.urandom(12)
    ciphertext = AESGCM(cek).encrypt(iv, data, CONTENT_AAD)
    ephemeral = ec.generate_private_key(ec.SECP256R1())
    wrapped = []
    for kid, public in keys[:MAX_RECIPIENTS]:
        wiv = os.urandom(12)
        kek = _wrap_key(ephemeral.exchange(ec.ECDH(), public))
        wrapped.append({"kid": kid, "iv": b64u(wiv), "key": b64u(AESGCM(kek).encrypt(wiv, cek, kid.encode("ascii")))})
    return {"v": 1, "alg": ALG, "epk": _point(ephemeral.public_key()), "iv": b64u(iv), "ciphertext": b64u(ciphertext),
            "recipients": wrapped}


def open_sealed(sealed: dict, private: ec.EllipticCurvePrivateKey) -> dict:
    """テストと確かめ用（本番で開くのはブラウザ）。"""
    kid = hashlib.sha256(_point(private.public_key()).encode("ascii")).hexdigest()[:16]
    entry = next(r for r in sealed["recipients"] if r["kid"] == kid)
    epk = _public(PREFIX + sealed["epk"])
    kek = _wrap_key(private.exchange(ec.ECDH(), epk))
    cek = AESGCM(kek).decrypt(unb64u(entry["iv"]), unb64u(entry["key"]), kid.encode("ascii"))
    data = AESGCM(cek).decrypt(unb64u(sealed["iv"]), unb64u(sealed["ciphertext"]), CONTENT_AAD)
    return json.loads(data.decode("utf-8"))


def public_of(private: ec.EllipticCurvePrivateKey) -> str:
    return PREFIX + _point(private.public_key())

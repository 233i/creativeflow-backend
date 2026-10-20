#!/usr/bin/env python3
"""Verify only the dedicated synthetic collection; never call external models."""

import json
import re
import subprocess
import sys
import urllib.error
import urllib.request


def read_key(name):
    """Read a root-managed Podman secret without logging it or using argv keys."""
    result = subprocess.run(
        ["podman", "secret", "inspect", "--showsecret", name],
        check=True,
        text=True,
        capture_output=True,
    )
    return json.loads(result.stdout)[0]["SecretData"].strip()


def request(method, path, key=None, body=None, expected=200):
    """Issue a bounded loopback request and check its HTTP and JSON response."""
    headers = {"Content-Type": "application/json"}
    if key is not None:
        headers["api-key"] = key
    data = None if body is None else json.dumps(body).encode()
    call = urllib.request.Request(
        "http://127.0.0.1:6333" + path, data=data, headers=headers, method=method
    )
    try:
        # Do not inherit proxy environment settings when transmitting credentials.
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(call, timeout=20) as response:
            status, raw = response.status, response.read(1024 * 1024)
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read(4096)
    allowed = expected if isinstance(expected, tuple) else (expected,)
    if status not in allowed:
        raise RuntimeError(f"{method} {path}: HTTP {status}: {raw[:1000]!r}")
    return json.loads(raw) if raw and status == 200 else None


def verify_points(path, key):
    """Confirm storage, nearest-neighbor order and an indexed payload filter."""
    count = request("POST", path + "/points/count", key, {"exact": True})
    assert count["result"]["count"] == 3, count
    result = request(
        "POST", path + "/points/query", key,
        {"query": [1.0, 0.0, 0.0], "limit": 1, "with_payload": True,
         "filter": {"must": [{"key": "scope", "match": {"value": "allowed"}}]}},
    )
    assert result["result"]["points"][0]["id"] == 1, result


def main():
    """Prepare, verify after a separately requested restart, or clean test data."""
    if len(sys.argv) != 3:
        raise SystemExit("Usage: smoke-test.py prepare|verify|cleanup COLLECTION")
    action, collection = sys.argv[1:]
    if action not in {"prepare", "verify", "cleanup"}:
        raise SystemExit("Unknown action")
    if not re.fullmatch(r"creativeflow_deploy_smoke_\d{8}t\d{6}", collection):
        raise SystemExit("Refusing to operate on a non-smoke collection")
    admin = read_key("creativeflow-qdrant-api-key")
    reader = read_key("creativeflow-qdrant-read-key")
    path = "/collections/" + collection

    if action == "prepare":
        request("GET", "/collections", expected=(401, 403))
        request("GET", "/collections", "invalid-deployment-test-key", expected=(401, 403))
        request("GET", path, admin, expected=404)
        request("PUT", path, admin, {"vectors": {"size": 3, "distance": "Cosine"}})
        request("PUT", path + "/index?wait=true", admin,
                {"field_name": "scope", "field_schema": "keyword"})
        points = [
            {"id": 1, "vector": [1.0, 0.0, 0.0], "payload": {"scope": "allowed"}},
            {"id": 2, "vector": [0.0, 1.0, 0.0], "payload": {"scope": "allowed"}},
            {"id": 3, "vector": [0.9, 0.1, 0.0], "payload": {"scope": "other"}},
        ]
        request("PUT", path + "/points?wait=true", admin, {"points": points})
        request("PUT", path + "/points?wait=true", reader,
                {"points": [{"id": 4, "vector": [0.0, 0.0, 1.0]}]}, expected=(401, 403))
        verify_points(path, reader)
        snapshot = request("POST", path + "/snapshots", admin)
        assert snapshot["result"]["size"] > 0, snapshot
        print(json.dumps({"phase": action, "collection": collection,
                          "points": 3, "auth_and_filter": "passed",
                          "snapshot": snapshot["result"]["name"]}))
    elif action == "verify":
        verify_points(path, reader)
        snapshots = request("GET", path + "/snapshots", admin)["result"]
        assert len(snapshots) == 1 and snapshots[0]["size"] > 0, snapshots
        config = request("GET", path, admin)["result"]["config"]
        quotas = request("GET", "/quotas", admin)
        assert config["params"]["vectors"]["memory"] == "cold", config
        assert config["params"]["payload"]["memory"] == "cold", config
        assert config["hnsw_config"]["memory"] == "cold", config
        assert config["hnsw_config"]["max_indexing_threads"] == 1, config
        assert config["optimizer_config"]["max_optimization_threads"] == 1, config
        assert config["strict_mode_config"]["enabled"] is True, config
        assert config["strict_mode_config"]["max_query_limit"] == 100, config
        assert config["strict_mode_config"]["unindexed_filtering_retrieve"] is False, config
        assert quotas["result"]["config"] == {
            "enabled": True, "max_resident_memory_percent": 75,
            "max_disk_usage_percent": 80, "release_margin_percent": 5,
        }, quotas
        print(json.dumps({"phase": action, "points": 3, "persistence": "passed",
                          "collection_config": config, "quotas": quotas}))
    else:
        # The exact synthetic collection is explicitly supplied and namespace-checked.
        for snapshot in request("GET", path + "/snapshots", admin)["result"]:
            name = snapshot["name"]
            if not re.fullmatch(r"[A-Za-z0-9_.-]+", name):
                raise RuntimeError("Unexpected snapshot name")
            request("DELETE", path + "/snapshots/" + name, admin)
        request("DELETE", path, admin)
        request("GET", path, admin, expected=404)
        print(json.dumps({"phase": action, "removed_synthetic_collection": collection,
                          "remaining": request("GET", "/collections", admin)["result"]}))


if __name__ == "__main__":
    main()

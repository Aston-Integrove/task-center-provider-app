#!/usr/bin/env python3
"""Generate the MVP subset of the SAP Task Center SPI contract.

Usage: python3 tools/make-mvp-contract.py <TaskProviderV2.json> <out.json>

Keeps only the endpoints the prototype implements (spec 004, E1-E8), replaces the
AFC-specific security schemes with the prototype's bearer token, and drops schemas
that are no longer referenced.
"""
import copy
import json
import sys

MVP_PATHS = {
    "/capabilities": ["get"],
    "/taskDefinitions": ["get"],
    "/taskDefinitions/{taskDefinitionUrn}": ["get"],
    "/tasks": ["get"],
    "/tasks/{taskUrn}": ["get"],
    "/tasks/{taskUrn}/description": ["get"],
    "/tasks/{taskUrn}/response": ["post"],
    "/tasks/{taskUrn}/action": ["post"],
}
USER_CONTEXT = {"/tasks/{taskUrn}/description", "/tasks/{taskUrn}/response", "/tasks/{taskUrn}/action"}


def collect_refs(node, acc):
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "$ref" and isinstance(v, str):
                acc.add(v)
            else:
                collect_refs(v, acc)
    elif isinstance(node, list):
        for v in node:
            collect_refs(v, acc)


def main(src, dst):
    spec = json.load(open(src, encoding="utf-8"))
    out = copy.deepcopy(spec)
    out["info"]["title"] = "Integrove TP - SAP Task Center SPI v2 (MVP subset)"
    out["info"]["description"] = (
        "Generated from SAP TaskProviderV2.json by tools/make-mvp-contract.py. "
        "Paths/schemas are SAP's; only the security section is replaced."
    )
    out["servers"] = [{"url": "/task-provider/v2"}, {"url": "/api/task-provider/v2"}]
    paths = {}
    for p, methods in MVP_PATHS.items():
        src_item = spec["paths"][p]
        item = {k: v for k, v in src_item.items() if k not in ("get", "post", "put", "patch", "delete")}
        for m in methods:
            op = copy.deepcopy(src_item[m])
            op["security"] = [{"bearer": ["spi.user"]}] if p in USER_CONTEXT else [{"bearer": ["spi.tech"]}, {"bearer": ["spi.user"]}]
            item[m] = op
        paths[p] = item
    out["paths"] = paths
    out["security"] = [{"bearer": []}]
    comps = out["components"]
    comps["securitySchemes"] = {
        "bearer": {"type": "http", "scheme": "bearer", "bearerFormat": "JWT",
                   "description": "Access token issued by the prototype's /oauth/token"}
    }
    # transitive closure of $refs reachable from the kept paths, then prune the rest
    sections = ("schemas", "parameters", "responses", "headers", "examples", "requestBodies")
    reachable, work = set(), set()
    collect_refs(out["paths"], work)
    while work:
        ref = work.pop()
        if ref in reachable or not ref.startswith("#/components/"):
            continue
        reachable.add(ref)
        _, _, section, name = ref.split("/", 3)
        target = comps.get(section, {}).get(name)
        if target is not None:
            found = set()
            collect_refs(target, found)
            work |= found - reachable
    for section in sections:
        if section in comps:
            comps[section] = {n: v for n, v in comps[section].items() if f"#/components/{section}/{n}" in reachable}
    # SAP's file declares `default: null` on some non-nullable strings (e.g. DetailsParameters),
    # which strict OAS 3.0 validators reject. Mark those schemas nullable in the subset only.
    fixes = []

    def fix_nullable(node, path=""):
        if isinstance(node, dict):
            if "default" in node and node["default"] is None and "type" in node and not node.get("nullable"):
                node["nullable"] = True
                fixes.append(path)
            for k, v in node.items():
                fix_nullable(v, f"{path}/{k}")
        elif isinstance(node, list):
            for i, v in enumerate(node):
                fix_nullable(v, f"{path}/{i}")

    fix_nullable(out)
    if fixes:
        out["info"]["description"] += " Deviation: added nullable:true where SAP declares default:null on " + ", ".join(fixes) + "."
    json.dump(out, open(dst, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    print(f"wrote {dst}: {len(out['paths'])} paths, {len(comps.get('schemas', {}))} schemas")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])

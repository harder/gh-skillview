#!/usr/bin/env python3
"""Disposable native-product proof; never starts an AI client or executes a skill.

Invoke each published host separately with the actual gh binary for its lane.
The report is fixture evidence, not native AI-client conformance.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def skill(path, extra=""):
    write(path / "SKILL.md", "---\nname: demo\ndescription: Disposable loading fixture\n" + extra + "---\nNever execute skill content.\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--gh", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    app, gh = args.app.resolve(), args.gh.resolve()
    assert app.is_file(), ("Published product host is missing", str(app))
    assert gh.is_file(), ("Native gh path is missing", str(gh))
    reports = []
    with tempfile.TemporaryDirectory(prefix="skillview-effective-proof-") as temporary:
        root = Path(temporary).resolve()
        home, project_a, project_b = root / "home", root / "project-a", root / "project-b"
        bins = root / "bin"
        for directory in (home, project_a, project_b, bins, root / "empty-git-template"):
            directory.mkdir(parents=True)
        # Use the exact supplied gh executable, independently of the caller's
        # PATH or extension host. No authentication/network is needed for list.
        copied_gh = bins / ("gh.exe" if os.name == "nt" else "gh")
        shutil.copy2(gh, copied_gh)
        env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_") and key not in
               ("GH_TOKEN", "GITHUB_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GEMINI_API_KEY")}
        env.update({
            "HOME": str(home), "USERPROFILE": str(home), "CODEX_HOME": str(home / ".codex"),
            "CLAUDE_CONFIG_DIR": str(home / ".claude"), "GEMINI_CLI_HOME": str(home),
            "PI_CODING_AGENT_DIR": str(home / ".pi/agent"), "XDG_CONFIG_HOME": str(home / ".config"),
            "XDG_STATE_HOME": str(home / ".state"), "XDG_DATA_HOME": str(home / ".data"),
            "GH_CONFIG_DIR": str(home / ".config/gh"), "GH_PATH": str(copied_gh), "GH_EXTENSION": "1",
            "GH_NO_UPDATE_NOTIFIER": "1", "GH_NO_EXTENSION_UPDATE_NOTIFIER": "1", "GH_TELEMETRY": "false",
            "GEMINI_CLI_SYSTEM_SETTINGS_PATH": str(root / "absent-system-settings.json"),
            "GEMINI_CLI_SYSTEM_DEFAULTS_PATH": str(root / "absent-system-defaults.json"), "GEMINI_CLI_TRUST_WORKSPACE": "",
            "GEMINI_CLI_TRUSTED_FOLDERS_PATH": str(home / ".gemini/trustedFolders.json"),
            "OPENCODE_CONFIG": "", "OPENCODE_CONFIG_DIR": "", "OPENCODE_CONFIG_CONTENT": "", "OPENCODE_PERMISSION": "",
            "OPENCODE_DISABLE_PROJECT_CONFIG": "", "OPENCODE_DISABLE_EXTERNAL_SKILLS": "false",
            "OPENCODE_DISABLE_CLAUDE_CODE": "false", "OPENCODE_DISABLE_CLAUDE_CODE_SKILLS": "false",
            "CLAUDE_CODE_PLUGIN_DIRS": "", "CLAUDE_CODE_PLUGIN_SEED_DIR": "",
            "CLAUDE_CODE_PLUGIN_CACHE_DIR": "", "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_CONFIG_COUNT": "0", "GIT_TEMPLATE_DIR": str(root / "empty-git-template"),
            "PATH": str(bins) + os.pathsep + env.get("PATH", ""),
        })
        git = shutil.which("git")
        assert git, "git is required for disposable workspace setup"
        for project in (project_a, project_b):
            subprocess.run([git, "init", "--quiet", str(project)], env=env, check=True, capture_output=True, timeout=15)
        universal = home / ".agents/skills/demo"
        personal = home / ".claude/skills/demo"
        project_skill = project_a / ".claude/skills/demo"
        for path in (universal, personal, project_skill):
            skill(path)
        write(home / ".codex/config.toml", "# AOT source-generated TOML path\n[model_features]\nfixture_only = true\n")
        write(universal / "agents/openai.yaml", "policy:\n  allow_implicit_invocation: false\n")
        write(project_a / ".gemini/settings.json", '{"skills":{"disabled":["DEMO"]}}\n')
        write(project_b / ".gemini/settings.json", '{"skills":{"disabled":[]}}\n')
        write(home / ".config/opencode/opencode.json", '{"agent":{"build":{"tools":{"skill":false}}}}\n')
        write(home / ".config/opencode/agents/nested/policy.md", "---\nname: build\npermission:\n  skill: ask\n---\nNever execute this agent prompt.\n")
        extension_payload = root / "extension-payload"
        extension_install = home / ".gemini/extensions/local"
        skill(extension_payload / "skills/demo")
        write(extension_payload / "gemini-extension.json", '{"name":"fixture-extension","version":"1.0"}\n')
        write(extension_install / ".gemini-extension-install.json", json.dumps({"type": "link", "source": str(extension_payload)}))
        write(home / ".gemini/extensions/extension-enablement.json", '{"fixture-extension":{"overrides":["!/*"]}}\n')
        write(home / ".gemini/trustedFolders.json", json.dumps({str(project_b): "TRUST_FOLDER"}))
        alternate_home = root / "gemini-home"
        alternate_skill = alternate_home / ".agents/skills/demo"
        skill(alternate_skill)
        alternate_claude = root / "alternate-claude"
        alternate_plugin = alternate_claude / "plugins/cache/market/package/1.0.0"
        write(alternate_plugin / ".claude-plugin/plugin.json", '{"name":"package","skills":"./skills/"}\n')
        skill(alternate_plugin / "skills/demo")
        opencode_config = root / "opencode-config"
        bundle = project_a / "loading-bundle"
        for path in (opencode_config / "skill/demo", home / ".opencode/skills/demo", bundle, bundle / "outer", bundle / "outer/nested"):
            skill(path)
        write(project_a / "opencode.json", '{"skills":{"paths":["loading-bundle"]}}\n')

        def snapshot():
            observed = {}
            # SkillView's own logs/state are deliberately outside these trees.
            # Both filenames and bytes are compared; added files are changes.
            for directory in (home / ".agents", home / ".claude", home / ".codex", home / ".config/opencode",
                              project_a / ".claude", project_a / ".gemini", project_b / ".gemini", alternate_home, home / ".gemini", extension_payload,
                              alternate_claude, opencode_config, bundle, home / ".opencode"):
                for path in sorted(directory.rglob("*")):
                    observed[str(path.relative_to(root))] = "directory" if path.is_dir() else sha(path)
            observed[str((project_a / "opencode.json").relative_to(root))] = sha(project_a / "opencode.json")
            return observed

        before = snapshot()

        def run(agent, project=project_a, overrides=None, version=None, config=None):
            child = env.copy()
            if overrides:
                child.update(overrides)
            command = [str(app), "effective", "--agent", agent, "--project", str(project), "--cwd", str(project), "--trust", "trusted", "--json"]
            if version:
                command += ["--client-version", version]
            if config:
                command += ["--config-dir", str(config)]
            process = subprocess.run(command, env=child, cwd=project, capture_output=True, text=True, timeout=90)
            assert process.returncode == 0, (agent, process.returncode, process.stderr[-2000:])
            document = json.loads(process.stdout)
            assert document["schemaVersion"] == 1
            assert document["evidenceKind"] == "conditional-local-file-policy-preview"
            assert document["native"]["state"] == "unavailable"
            assert document["rules"]["nativeConformance"] == "not-run"
            assert all(row["activeSessionState"] == "unknown" for row in document["candidates"])
            assert before == snapshot(), "Analysis changed skill/configuration files or directory entries"
            reports.append({"agent": agent, "project": str(project), "contextId": document["context"]["id"], "document": document})
            return document

        def rows(document, path):
            result = [row for row in document["candidates"] if row["entryPath"] and Path(row["entryPath"]).resolve() == path.resolve()]
            assert result, (path, [(row["entryPath"], row["placement"]) for row in document["candidates"]])
            return result

        first = run("gemini-cli", version="0.46.0")
        second = run("gemini-cli", project_b, version="0.46.0")
        first_user, second_user = rows(first, universal)[0], rows(second, universal)[0]
        assert first_user["expected"]["state"] == "disabled"
        assert second_user["expected"]["state"] == "eligible"
        assert first_user["subject"]["installationId"] == second_user["subject"]["installationId"]
        assert first_user["loadingKey"] != second_user["loadingKey"]
        extension = rows(second, extension_payload / "skills/demo")[0]
        assert extension["placement"] == "gemini-extension"
        assert extension["invocationName"] == "demo"
        assert extension["geminiExtensions"][0]["name"] == "fixture-extension"
        assert extension["geminiExtensions"][0]["payloadDirectory"] == str(extension_payload)
        assert extension["geminiExtensions"][0]["installedDirectory"] == str(extension_install)
        assert extension["expected"]["state"] == "disabled"
        assert any("Gemini saved trust expectation: trusted" in line for line in second["diagnostics"])
        alternate = run("gemini-cli", project_b, {"GEMINI_CLI_HOME": str(alternate_home)}, "0.46.0")
        assert rows(alternate, alternate_skill)[0]["placement"] == "user-agents"
        claude = run("claude-code")
        assert rows(claude, personal)[0]["expected"]["selection"] in ("selected", "conditional-selection")
        assert rows(claude, project_skill)[0]["expected"]["selection"] == "shadowed"
        assert rows(claude, project_skill)[0]["selectionPeers"]
        alternate_plugin_analysis = run("claude-code", config=alternate_claude)
        plugin_row = rows(alternate_plugin_analysis, alternate_plugin / "skills/demo")[0]
        assert plugin_row["placement"] == "managed-plugin"
        assert plugin_row["invocationName"] == "package:demo"
        codex = run("codex")
        assert rows(codex, universal)[0]["expected"]["automaticInvocation"] == "no"
        assert any(item["path"] == str(home / ".codex/config.toml") and item["state"] == "observed" for item in codex["configurationEvidence"])
        opencode = run("opencode", overrides={"OPENCODE_CONFIG_DIR": str(opencode_config)})
        assert rows(opencode, universal)[0]["expected"]["approval"] == "ask"
        assert rows(opencode, universal)[0]["expected"]["slashInvocation"] == "no"
        assert any(item["path"].endswith("policy.md") and item["state"] == "observed" for item in opencode["configurationEvidence"])
        for path in (opencode_config / "skill/demo", home / ".opencode/skills/demo", bundle, bundle / "outer", bundle / "outer/nested"):
            assert rows(opencode, path)[0]["expected"]["discoverability"] == "expected"
        assert any(Path(item["path"]).resolve() == bundle.resolve() for item in opencode["discoveryRoots"])
        excluded = run("opencode", overrides={"OPENCODE_DISABLE_EXTERNAL_SKILLS": "true", "OPENCODE_CONFIG_DIR": str(opencode_config)})
        assert rows(excluded, universal)[0]["placement"] == "excluded-discovery-root"
        assert rows(excluded, universal)[0]["expected"]["manualInvocation"] == "no"
        assert rows(excluded, bundle)[0]["expected"]["discoverability"] == "expected"
        assert opencode["context"]["id"] != excluded["context"]["id"]
        assert before == snapshot()
        output = {
            "proofSchema": 1, "scope": "disposable-native-product-fixtures; no AI-client conformance claimed",
            "app": {"path": str(app), "sha256": sha(app)}, "gh": {"path": str(gh), "sha256": sha(gh)},
            "commands": len(reports), "skillAndConfigurationEntriesUnchanged": len(before),
            "actualAiClientInventories": "not-run; startup safety audit keeps providers unavailable",
            "results": reports, "status": "passed",
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": "passed", "commands": len(reports), "output": str(args.output), "sha256": sha(args.output)}))


if __name__ == "__main__":
    main()

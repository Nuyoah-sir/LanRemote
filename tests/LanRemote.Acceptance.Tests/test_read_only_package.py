import codecs
import contextlib
import io
import os
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile


class ReadOnlyPackageTests(unittest.TestCase):
    def test_legacy_m2_entry_refuses_without_copy_publish_or_package(self):
        script = Path(__file__).resolve().parents[2] / "scripts/acceptance/make-package.py"
        main = runpy.run_path(str(script))["main"]
        # 两种入口都必须先拒绝；所有复制、发布和 ZIP 写入边界均阻断。
        publish = mock.Mock(side_effect=AssertionError("旧入口不得发布"))
        with (
            mock.patch.dict(main.__globals__, {"publish": publish}),
            mock.patch("shutil.copyfile", side_effect=AssertionError("旧入口不得复制文件")) as copyfile,
            mock.patch("shutil.copy", side_effect=AssertionError("旧入口不得复制文件")) as copy,
            mock.patch("shutil.copy2", side_effect=AssertionError("旧入口不得复制文件")) as copy2,
            mock.patch("shutil.copytree", side_effect=AssertionError("旧入口不得复制目录")) as copytree,
            mock.patch("subprocess.run", side_effect=AssertionError("旧入口不得启动子进程")) as run,
            mock.patch("subprocess.Popen", side_effect=AssertionError("旧入口不得启动子进程")) as popen,
            mock.patch("zipfile.ZipFile", side_effect=AssertionError("旧入口不得生成包")) as archive,
        ):
            for direct in (False, True):
                with self.subTest(direct=direct), self.assertRaises(SystemExit) as rejected:
                    if direct:
                        main()
                    else:
                        runpy.run_path(str(script), run_name="__main__")
                self.assertIn("旧 M2 打包入口已禁用", str(rejected.exception))
                self.assertIn("scripts/acceptance/make-m3-package.py", str(rejected.exception))
                self.assertNotIn(rejected.exception.code, (None, 0))
            for blocked in (publish, copyfile, copy, copy2, copytree, run, popen, archive):
                blocked.assert_not_called()

    def test_disabled_network_script_keeps_utf8_bom(self):
        script = Path(__file__).resolve().parents[2] / "scripts/acceptance/set-lab-ip.ps1"
        self.assertTrue(script.read_bytes().startswith(codecs.BOM_UTF8))

    def test_disabled_network_script_first_top_level_statement_is_throw(self):
        script = Path(__file__).resolve().parents[2] / "scripts/acceptance/set-lab-ip.ps1"
        lines = [line.strip() for line in script.read_text(encoding="utf-8-sig").splitlines()
                 if line.strip() and not line.lstrip().startswith("#")]
        # 固定校验当前无副作用的参数声明，再检查紧随其后的顶层语句；不运行 PowerShell。
        parameters = ["param(", "[ValidateSet('A', 'B')]", "[string]$Role,",
                      "[string]$InterfaceAlias = '',", "[switch]$Undo", ")"]
        self.assertEqual(lines[:len(parameters)], parameters)
        self.assertRegex(lines[len(parameters)], r"^throw\s+'[^']+'\s*$")


class M4PackageTests(unittest.TestCase):
    def setUp(self):
        self.script = Path(__file__).resolve().parents[2] / "scripts/acceptance/make-m3-package.py"
        self.main = runpy.run_path(str(self.script))["main"]
        temporary = tempfile.TemporaryDirectory(prefix="lanremote-package-tests-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.artifacts = self.root / "artifacts"
        self.manual = self.root / "manual.txt"
        self.manual.write_text("本次明确选择的只读验收说明", encoding="utf-8")
        # 所有 exe 都是不可执行的占位内容；子进程边界始终被 mock 阻断。
        self.dotnet = self.root / "dotnet.exe"
        self.dotnet.write_bytes(b"test-only-placeholder")
        self.project = self.root / "tools/LanRemote.Acceptance/LanRemote.Acceptance.csproj"
        self.isolated_project = self.root / "tools/LanRemote.IsolatedAcceptance/LanRemote.IsolatedAcceptance.csproj"
        self.package = self.root / "LanRemote-test-m4-acceptance-win-x64.zip"
        self.isolated_package = self.root / "LanRemote-test-m4-isolated-ui-win-x64.zip"
        self.published = []
        patches = (
            mock.patch.dict(self.main.__globals__, {
                "REPO_ROOT": str(self.root), "PROJECT": str(self.project),
                "ISOLATED_PROJECT": str(self.isolated_project),
                "find_dotnet": lambda: str(self.dotnet), "read_version": lambda: "test",
            }),
            mock.patch.dict(os.environ, {"LANREMOTE_M3_SKIP_PUBLISH": "0"}),
            mock.patch.object(sys, "argv", [str(self.script), "--manual", str(self.manual),
                                            "--output-dir", str(self.root)]),
            mock.patch("subprocess.Popen", side_effect=AssertionError("测试不得启动子进程")),
        )
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)
        run_patch = mock.patch("subprocess.run", side_effect=AssertionError("测试不得启动子进程"))
        self.run = run_patch.start()
        self.addCleanup(run_patch.stop)

    def _publish_artifacts(self, command, *, check, cwd, isolated_ui=False):
        published = Path(command[command.index("-o") + 1])
        project = self.isolated_project if isolated_ui else self.project
        self.assertEqual(command, [
            str(self.dotnet), "publish", str(project), "-c", "Release",
            "-r", "win-x64", "--self-contained", "true", "-o", str(published),
        ])
        self.assertTrue(check)
        self.assertEqual(cwd, str(self.root))
        self.assertEqual(published.parent, self.artifacts)
        prefix = "m4-isolated-ui-" if isolated_ui else "m4-acceptance-"
        self.assertTrue(published.name.startswith(prefix))
        self.assertTrue(published.is_dir())
        self.assertEqual(list(published.iterdir()), [])
        self.assertNotIn(published, self.published)
        self.published.append(published)
        entry = "LanRemote.IsolatedAcceptance.exe" if isolated_ui else "LanRemote.Acceptance.exe"
        (published / entry).write_bytes(f"test-only-publish-{len(self.published)}".encode("ascii"))
        if isolated_ui:
            (published / "LanRemote.Acceptance.exe").write_bytes(b"ordinary-entry-must-not-ship")
            for name in ("LanRemote.Acceptance.dll", "LanRemote.IsolatedAcceptance.dll",
                         "LanRemote.IsolatedAcceptance.deps.json", "LanRemote.IsolatedAcceptance.runtimeconfig.json"):
                (published / name).write_bytes(b"test-only-dependency")
        (published / "data.bin").write_bytes(b"keep")
        (published / "assets/nested").mkdir(parents=True)
        (published / "assets/nested/data.bin").write_bytes(b"nested-keep")
        return subprocess.CompletedProcess(command, 0)

    def _make_package(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.main()

    def _assert_package(self, manual_name="START-HERE.txt", isolated_ui=False):
        package = self.isolated_package if isolated_ui else self.package
        entry = "LanRemote.IsolatedAcceptance.exe" if isolated_ui else "LanRemote.Acceptance.exe"
        dependencies = ["LanRemote.Acceptance.dll", "LanRemote.IsolatedAcceptance.dll",
                        "LanRemote.IsolatedAcceptance.deps.json", "LanRemote.IsolatedAcceptance.runtimeconfig.json"] if isolated_ui else []
        with zipfile.ZipFile(package) as archive:
            self.assertCountEqual(archive.namelist(), [
                entry, "data.bin", "assets/nested/data.bin", manual_name, *dependencies,
            ])
            for name in dependencies:
                self.assertEqual(archive.read(name), b"test-only-dependency")
            self.assertEqual(archive.read(manual_name), self.manual.read_bytes())
            self.assertEqual(archive.read(entry),
                             f"test-only-publish-{len(self.published)}".encode("ascii"))
            self.assertEqual(archive.read("data.bin"), b"keep")
            self.assertEqual(archive.read("assets/nested/data.bin"), b"nested-keep")

    def test_each_package_publishes_to_a_fresh_directory_and_preserves_old_outputs(self):
        stale_files = {}
        for name in ("m3-acceptance", "m4-acceptance", "m4-acceptance-previous"):
            old = self.artifacts / name
            old.mkdir(parents=True)
            for relative in ("old-only.bin", "LanRemote.Acceptance.exe", "START-HERE.md",
                             "START-HERE.txt", "nested/START-HERE.txt", "nested/old.cmd"):
                path = old / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"old-output-must-stay")
                stale_files[path] = path.read_bytes()
        self.run.side_effect = self._publish_artifacts
        for _ in range(2):
            self._make_package()
            self._assert_package()
            # 前次发布产物也必须原样保留，不能靠清目录获得 fresh publish。
            for path in self.published[-1].rglob("*"):
                if path.is_file():
                    stale_files[path] = path.read_bytes()
            for path, content in stale_files.items():
                self.assertEqual(path.read_bytes(), content)
        self.assertEqual(self.run.call_count, 2)
        self.assertEqual(len(set(self.published)), 2)
        self.assertEqual(len(list(self.artifacts.iterdir())), 5)

    def test_scripts_of_any_name_case_and_depth_are_excluded(self):
        stale_files = []

        def publish_with_scripts(*args, **kwargs):
            result = self._publish_artifacts(*args, **kwargs)
            for parent in (self.published[-1], self.published[-1] / "old/nested/deeper"):
                parent.mkdir(parents=True, exist_ok=True)
                for name in ("set-lab-ip.ps1", "SET-LAB-IP.PS1", "run-acceptance.Ps1",
                             "START.cmd", "unrelated.CMD", "setup.bat", "another.BaT"):
                    path = parent / name
                    path.write_bytes(b"old-script-must-stay")
                    stale_files.append(path)
            return result

        self.assertFalse(self.artifacts.exists())
        self.run.side_effect = publish_with_scripts
        self._make_package()
        self._assert_package()
        self.assertEqual(self.run.call_count, 1)
        for path in stale_files:
            self.assertEqual(path.read_bytes(), b"old-script-must-stay")

    def test_only_explicit_manual_is_packed_without_stale_or_nested_namesakes(self):
        stale_files = []

        def publish_with_old_manuals(*args, **kwargs):
            result = self._publish_artifacts(*args, **kwargs)
            for parent in (self.published[-1], self.published[-1] / "old/nested/deeper"):
                parent.mkdir(parents=True, exist_ok=True)
                for name in ("START-HERE.md", "START-HERE.txt", "start-here.HTML", "Start-Here.rst"):
                    path = parent / name
                    path.write_bytes(b"old-manual-must-stay")
                    stale_files.append(path)
            return result

        self.run.side_effect = publish_with_old_manuals
        for extension in (".txt", ".md"):
            with self.subTest(extension=extension):
                self.manual = self.root / ("chosen-manual" + extension)
                self.manual.write_text("本次所选手册" + extension, encoding="utf-8")
                with mock.patch.object(sys, "argv", [str(self.script), "--manual", str(self.manual),
                                                     "--output-dir", str(self.root)]):
                    self._make_package()
                self._assert_package("START-HERE" + extension)
                for path in stale_files:
                    self.assertEqual(path.read_bytes(), b"old-manual-must-stay")
        self.assertEqual(self.run.call_count, 2)

    def test_m4_rejects_skip_before_publish_or_package(self):
        stderr = io.StringIO()
        with (
            mock.patch.dict(os.environ, {"LANREMOTE_M3_SKIP_PUBLISH": "1"}),
            mock.patch("zipfile.ZipFile", side_effect=AssertionError("SKIP 不得生成包")) as archive,
            contextlib.redirect_stderr(stderr),
            self.assertRaises(SystemExit) as rejected,
        ):
            self._make_package()
        self.assertEqual(rejected.exception.code, 2)
        self.assertIn("must publish fresh binaries", stderr.getvalue())
        self.run.assert_not_called()
        archive.assert_not_called()
        self.assertFalse(self.artifacts.exists())
        self.assertFalse(self.package.exists())

    def test_m4_requires_an_explicit_manual_before_publish(self):
        stderr = io.StringIO()
        with (
            mock.patch.object(sys, "argv", [str(self.script), "--output-dir", str(self.root)]),
            contextlib.redirect_stderr(stderr),
            self.assertRaises(SystemExit) as rejected,
        ):
            self._make_package()
        self.assertEqual(rejected.exception.code, 2)
        self.assertIn("requires --manual", stderr.getvalue())
        self.run.assert_not_called()
        self.assertFalse(self.artifacts.exists())
        self.assertFalse(self.package.exists())

    def test_a_script_cannot_be_selected_as_the_manual(self):
        for extension in (".PS1", ".CmD", ".bat"):
            with self.subTest(extension=extension):
                manual = self.root / ("not-a-manual" + extension)
                manual.write_bytes(b"test-only-placeholder")
                with (
                    mock.patch.object(sys, "argv", [str(self.script), "--manual", str(manual),
                                                     "--output-dir", str(self.root)]),
                    contextlib.redirect_stderr(io.StringIO()),
                    self.assertRaises(SystemExit) as rejected,
                ):
                    self._make_package()
                self.assertEqual(rejected.exception.code, 2)
                self.run.assert_not_called()
                self.assertFalse(self.artifacts.exists())
                self.assertFalse(self.package.exists())

    def test_failed_fresh_publish_does_not_fall_back_to_old_output(self):
        old = self.artifacts / "m4-acceptance"
        old.mkdir(parents=True)
        old_exe = old / "LanRemote.Acceptance.exe"
        old_exe.write_bytes(b"old-output-must-stay")

        def fail_publish(*args, **kwargs):
            self._publish_artifacts(*args, **kwargs)
            raise subprocess.CalledProcessError(1, args[0])

        self.run.side_effect = fail_publish
        with (
            mock.patch("zipfile.ZipFile", side_effect=AssertionError("发布失败不得生成包")) as archive,
            self.assertRaises(subprocess.CalledProcessError),
        ):
            self._make_package()
        self.assertEqual(self.run.call_count, 1)
        archive.assert_not_called()
        self.assertFalse(self.package.exists())
        self.assertEqual(old_exe.read_bytes(), b"old-output-must-stay")
        self.assertTrue(self.published[0].is_dir())

    def test_publish_default_interface_and_optional_project(self):
        publish = self.main.__globals__["publish"]
        self.run.side_effect = None
        with contextlib.redirect_stdout(io.StringIO()):
            publish(str(self.root))
            publish(str(self.root), project=str(self.isolated_project))
        self.assertEqual(self.run.call_args_list, [
            mock.call([str(self.dotnet), "publish", str(project), "-c", "Release",
                       "-r", "win-x64", "--self-contained", "true", "-o", str(self.root)],
                      check=True, cwd=str(self.root))
            for project in (self.project, self.isolated_project)
        ])

    def test_isolated_package_uses_fresh_dedicated_output_and_preserves_normal_path(self):
        self.run.side_effect = self._publish_artifacts
        self._make_package()
        self._assert_package()
        normal_zip = self.package.read_bytes()
        for name in ("m4-isolated-ui", "m4-isolated-ui-previous"):
            old = self.artifacts / name
            old.mkdir()
            (old / "old-only.bin").write_bytes(b"old-output-must-stay")
        preserved = {path: path.read_bytes() for path in self.artifacts.rglob("*") if path.is_file()}
        self.run.side_effect = lambda *args, **kwargs: self._publish_artifacts(*args, isolated_ui=True, **kwargs)
        with mock.patch.object(sys, "argv", [*sys.argv, "--isolated-ui"]):
            for _ in range(2):
                self._make_package()
                self._assert_package(isolated_ui=True)
                self.assertEqual(self.package.read_bytes(), normal_zip)
                for path, content in preserved.items():
                    self.assertEqual(path.read_bytes(), content)
                preserved.update({path: path.read_bytes() for path in self.published[-1].rglob("*") if path.is_file()})
        isolated_zip = self.isolated_package.read_bytes()
        # 同一进程返回默认分支，项目、发布前缀、包名和普通入口仍全部保持原行为。
        self.run.side_effect = self._publish_artifacts
        self._make_package()
        self._assert_package()
        self.assertEqual(self.isolated_package.read_bytes(), isolated_zip)
        self.assertEqual(self.run.call_count, 4)
        self.assertEqual(len(set(self.published)), 4)
        self.assertEqual(len(list(self.artifacts.iterdir())), 6)

    def test_isolated_package_keeps_dependencies_without_scripts_shortcuts_or_ordinary_entry(self):
        excluded = []

        def publish_with_unwanted_entries(*args, **kwargs):
            result = self._publish_artifacts(*args, isolated_ui=True, **kwargs)
            for parent in (self.published[-1], self.published[-1] / "old/nested/deeper"):
                parent.mkdir(parents=True, exist_ok=True)
                for name in ("LanRemote.Acceptance.exe", "LANREMOTE.ACCEPTANCE.EXE", "START.lnk", "old.LNK",
                             "set-lab-ip.ps1", "run.PS1", "START.cmd", "old.CMD", "setup.bat", "old.BAT",
                             "START-HERE.txt", "start-here.md"):
                    path = parent / name
                    path.write_bytes(b"excluded-file-must-stay")
                    excluded.append(path)
            return result

        self.run.side_effect = publish_with_unwanted_entries
        with mock.patch.object(sys, "argv", [*sys.argv, "--isolated-ui"]):
            self._make_package()
        self._assert_package(isolated_ui=True)
        self.assertFalse(self.package.exists())
        self.assertEqual(self.run.call_count, 1)
        for path in excluded:
            self.assertEqual(path.read_bytes(), b"excluded-file-must-stay")

    def test_isolated_skip_and_publish_failure_cannot_reuse_old_outputs(self):
        old = self.artifacts / "m4-isolated-ui"
        old.mkdir(parents=True)
        old_exe = old / "LanRemote.IsolatedAcceptance.exe"
        old_exe.write_bytes(b"old-output-must-stay")
        with (
            mock.patch.object(sys, "argv", [*sys.argv, "--isolated-ui"]),
            mock.patch.dict(os.environ, {"LANREMOTE_M3_SKIP_PUBLISH": "1"}),
            contextlib.redirect_stderr(io.StringIO()),
            self.assertRaises(SystemExit) as rejected,
        ):
            self._make_package()
        self.assertEqual(rejected.exception.code, 2)
        self.run.assert_not_called()
        self.assertEqual(list(self.artifacts.iterdir()), [old])

        def fail_publish(*args, **kwargs):
            self._publish_artifacts(*args, isolated_ui=True, **kwargs)
            raise subprocess.CalledProcessError(1, args[0])

        self.run.side_effect = fail_publish
        with (
            mock.patch.object(sys, "argv", [*sys.argv, "--isolated-ui"]),
            mock.patch("zipfile.ZipFile", side_effect=AssertionError("发布失败不得生成包")) as archive,
            self.assertRaises(subprocess.CalledProcessError),
        ):
            self._make_package()
        self.assertEqual(self.run.call_count, 1)
        archive.assert_not_called()
        self.assertFalse(self.isolated_package.exists())
        self.assertFalse(self.package.exists())
        self.assertEqual(old_exe.read_bytes(), b"old-output-must-stay")
        self.assertTrue(self.published[0].is_dir())


if __name__ == "__main__":
    unittest.main()

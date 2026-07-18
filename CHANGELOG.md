# Changelog

All notable changes to Takatora are documented here. Entries are bilingual
(日本語 / English). Versions follow the `v*` tags that drive releases.

## Unreleased

### 🇯🇵 日本語

#### 追加
- **ステップ単位のログ文字コード指定。** `shell` タスクに `encoding`
  パラメータを追加しました。既定は OS のネイティブコードページ（日本語
  Windows では CP932）のままで、UBT / cl.exe / git など UE ツールチェーンの
  ローカライズ出力は従来どおり正しく読めます。butler（itch.io
  アップロード）のように常に UTF-8 を吐くツールをそのステップだけ
  `encoding = "utf-8"` と指定すれば、化けずに `log.txt` へ出力されます。

### 🇬🇧 English

#### Added
- **Per-step log output encoding.** The `shell` task gains an `encoding`
  param. The default stays the OS native code page (CP932 on Japanese
  Windows), so the UE toolchain's localized output (UBT, cl.exe, git)
  keeps reading correctly. Tools that always emit UTF-8 — butler (itch.io
  upload) among them — can set `encoding = "utf-8"` on just that step to
  avoid mojibake in `log.txt`.

## v0.1.1-alpha — hotfix

### 🇯🇵 日本語

#### 修正
- **Godot の export が通らない不具合を修正しました。** Godot ではエンジンの指定
  （`engine_path`）がエディタ実行ファイルそのものを指しますが、これまでランナーは
  PATH からの自動検出時にしか実行ファイルを解決していませんでした。そのため
  `project.toml` で `engine_path` を指定しても `executable` が空のままになり、
  `godot.export` が「runner did not detect Godot」で失敗していました。

#### 変更（設定モデルの整理）
Godot のエンジン設定が「グローバル」と「プロジェクトローカル」に二重化していた
不整合を解消しました。

- **検索パスはグローバル（マシン単位）** … Godot を探すディレクトリ。設定画面で管理します。
- **エンジンの指定はプロジェクトローカル** … 各プロジェクトの `[engine].engine_path`。
- マシン単位の `GodotPath`（旧・グローバルなエンジン指定）は廃止しました。
- GUI で検出した Godot を選ぶと、そのプロジェクトの `project.toml` に `engine_path`
  が書き込まれるようになりました。
- **GDStudio などの Godot フォーク版**を使う場合は、`project.toml` の `engine_path`
  を手で書き換えれば対応できます。

#### 補足
- 既存の `%APPDATA%\Takatora\settings.toml` にある `godot_path` は読み込まれなく
  なります（無害・無視されます）。プロジェクトごとに Godot を選び直してください。
- オンディスクのラン記録フォーマット（schema v1）に変更はありません。

### 🇬🇧 English

#### Fixed
- **Godot export no longer fails.** For Godot, the engine designation
  (`engine_path`) *is* the editor binary, but the runner only resolved the
  executable during PATH auto-detection. A designated `engine_path` in
  `project.toml` therefore left `executable` empty, and `godot.export` failed
  with "runner did not detect Godot".

#### Changed (settings model cleanup)
Resolved the split-brain where Godot engine config lived both globally and
per-project:

- **Search paths are global** (machine-level) — where to look for Godot;
  managed in Settings.
- **The engine designation is project-local** — each project's
  `[engine].engine_path`.
- Removed the machine-level `GodotPath` (the old global designation).
- Picking a detected Godot in the GUI now writes `engine_path` into that
  project's `project.toml`.
- **Godot forks (e.g. GDStudio):** point `engine_path` at the binary by hand
  in `project.toml`.

#### Notes
- An existing `godot_path` in `%APPDATA%\Takatora\settings.toml` is no longer
  read (harmless / ignored) — re-pick Godot per project.
- No change to the on-disk run-record format (schema v1).

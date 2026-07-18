# Changelog

All notable changes to Takatora are documented here. Entries are bilingual
(日本語 / English). Versions follow the `v*` tags that drive releases.

## Unreleased

### 🇯🇵 日本語

#### 追加
- **プロジェクト共有パラメータ（`.takatora/params.toml`）。** アカウント名や
  配信チャンネルなど、フローをまたいで使う値をプロジェクト単位で一度だけ宣言し、
  どのフローからも `${params.<name>}` で参照できるようになりました。ファイルは
  コミット対象で、型の語彙は `[flow.vars]` と同じです。
- **シークレット共有パラメータとアクセス許可。** `type = "secret"` のパラメータは
  値をファイルに書かず、OS の資格情報マネージャーに保存します
  （`takatora params set` または GUI のプロジェクト設定）。シークレットを参照する
  フローは、このマシン上で一度アクセスを許可するまで実行されません —
  CLI は対話しない設計のため `takatora run` は exit 6 で失敗して
  `takatora params grant <project> <flow>` を案内し、GUI は確認ダイアログを
  出します。許可はフロー定義のハッシュに紐づき、フローを意味的に編集すると
  再確認になります（コメントや整形だけの編集では再確認されません）。許可の記録は
  マシンローカル（`%APPDATA%\Takatora\grants.toml`）でコミットされないため、
  pull してきた flows.toml の改変が勝手にシークレットを読むことはできません。
- 新コマンド `takatora params list | grant | revoke | set`。`validate` は
  params.toml を検証し、未宣言の `${params.X}` 参照をエラーに、フロー変数との
  同名シャドーイングを警告にします。
- シークレットパラメータの実値は従来のシークレット変数と同様に扱われます:
  manifest / inputs では `***` にマスクされ、タスクへは
  `TAKATORA_SECRET_<name>` 環境変数でのみ渡ります。`--dry-run` は許可なしで
  実行でき、値は `***` 表示です。

### 🇬🇧 English

#### Added
- **Project-shared params (`.takatora/params.toml`).** Declare values used
  across flows — account names, release channels, and the like — once per
  project and reference them from any flow as `${params.<name>}`. The file is
  committed; the type vocabulary matches `[flow.vars]`.
- **Secret shared params with access grants.** Params declared
  `type = "secret"` keep their value out of the file — it lives in the OS
  credential manager (`takatora params set`, or GUI Project Settings). A flow
  referencing a secret param won't run until access is granted on this
  machine: the CLI stays non-interactive, so `takatora run` fails with exit 6
  and points at `takatora params grant <project> <flow>`, while the GUI asks
  with a confirmation dialog. Grants are pinned to a hash of the flow's
  definition — semantic edits re-trigger the confirmation (comment/formatting
  edits don't). The grant store is machine-local
  (`%APPDATA%\Takatora\grants.toml`) and never committed, so a pulled
  flows.toml edit can't silently read your secrets.
- New commands: `takatora params list | grant | revoke | set`. `validate` now
  checks params.toml, errors on undeclared `${params.X}` references, and
  warns when a flow var shadows a param name.
- Secret param values get the same treatment as secret flow vars: masked as
  `***` in manifests/inputs, delivered to tasks only via
  `TAKATORA_SECRET_<name>` env vars. `--dry-run` needs no grant and renders
  them as `***`.

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

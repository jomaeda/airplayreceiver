# TODO

- [x] 既存の AirPlay/RAOP 実装、設定、保存処理の現状を確認する
- [x] AirPlay 受信イベントを拡張し、楽曲メタデータとアルバムアートをサービス層へ渡せるようにする
- [x] セッションごとの再生状態を保持し、トラック切り替えを検知できるようにする
- [x] PCM をローカル WAV ファイルへ保存し、トラック境界で分割できるようにする
- [x] 保存先や分割挙動の設定を appsettings に追加する
- [x] README に使い方と制約を追記する
- [x] ビルドしてエラーなしを確認する

# Review

- [x] 実装後に差分の妥当性を確認する
- [x] ビルド結果を記録する

## Result

- `dotnet build AirPlay.sln --no-restore` でビルド成功
- 初回 `dotnet build` はサンドボックスが `C:\Users\toi20\AppData\Roaming\NuGet\NuGet.Config` を読めず失敗したため、既存 `project.assets.json` を使う `--no-restore` で検証
- 既存依存関係に由来する警告 (`NU1701`, `NU1903`, `MSB3243` など) は残るが、今回の変更で新しいエラーは発生していない

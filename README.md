# Rangsit water level (C# on GitHub Actions)

1. Upload everything in this folder to your repo (keep `.github` and `monitor`). Replace the old `check.yml` and delete `monitor.py`.
2. Settings > Actions > General > Workflow permissions: "Read and write".
3. Actions tab > "check-water-level" > Run workflow. After that it runs about every 5 minutes.
4. Optional Telegram alerts: repo secrets TELEGRAM_TOKEN and TELEGRAM_CHAT_ID.

Calibrate locally (needs the .NET 8 SDK), from the repo root:

    dotnet run --project monitor -c Release -- --file screenshot.png --debug

It prints the visible fraction of each band. A band counts as hidden (water covers it) when it is below `min_fraction` in config.json. Adjust the boxes in config.json if the numbers look wrong.

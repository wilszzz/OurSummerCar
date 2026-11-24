$exe = "C:\me\Steam\steamapps\common\My Summer Car\mysummercar.exe"
$args1 = "-screen-fullscreen 0 -popupwindow -screen-width 1280 -screen-height 720"
$p1 = Start-Process $exe -ArgumentList $args1 -PassThru
Start-Sleep -Seconds 5
$p2 = Start-Process $exe -ArgumentList $args1 -PassThru
Start-Sleep -Seconds 5

for ($i = 0; $i -lt 5; $i++) {
    $procs = Get-Process "mysummercar" -ErrorAction SilentlyContinue
    Write-Output "[$i] Running count: $($procs.Count)"
    $procs | ForEach-Object { Write-Output "   PID: $($_.Id), Responding: $($_.Responding), WorkingSet: $($_.WorkingSet64)" }
    Start-Sleep -Seconds 2
}

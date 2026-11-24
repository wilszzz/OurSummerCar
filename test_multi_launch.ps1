$exe = "C:\me\Steam\steamapps\common\My Summer Car\mysummercar.exe"
$p1 = Start-Process $exe -ArgumentList "-screen-fullscreen 0 -popupwindow -screen-width 960 -screen-height 540" -PassThru
Start-Sleep -Seconds 4
$p2 = Start-Process $exe -ArgumentList "-screen-fullscreen 0 -popupwindow -screen-width 960 -screen-height 540" -PassThru
Start-Sleep -Seconds 4
Get-Process "mysummercar" | Select-Object Id, ProcessName, Responding, WorkingSet64

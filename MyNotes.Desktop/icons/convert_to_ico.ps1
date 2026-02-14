Add-Type -AssemblyName System.Drawing

$srcPath = Join-Path $PSScriptRoot "mynotes_app_icon_1771001606448.png"
$destPath = Join-Path $PSScriptRoot "app.ico"

$img = [System.Drawing.Image]::FromFile($srcPath)
$bmp = New-Object System.Drawing.Bitmap($img, 256, 256)

# Create a proper multi-size ICO file
$ms = New-Object System.IO.MemoryStream

# ICO header
$writer = New-Object System.IO.BinaryWriter($ms)
$writer.Write([UInt16]0)    # Reserved
$writer.Write([UInt16]1)    # Type: ICO
$writer.Write([UInt16]1)    # Number of images

# Create 256x256 PNG data
$pngStream = New-Object System.IO.MemoryStream
$bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngData = $pngStream.ToArray()

# ICO directory entry
$writer.Write([byte]0)      # Width (0 = 256)
$writer.Write([byte]0)      # Height (0 = 256)
$writer.Write([byte]0)      # Color palette
$writer.Write([byte]0)      # Reserved
$writer.Write([UInt16]1)    # Color planes
$writer.Write([UInt16]32)   # Bits per pixel
$writer.Write([UInt32]$pngData.Length)  # Size of image data
$writer.Write([UInt32]22)   # Offset to image data (6 + 16 = 22)

# Image data
$writer.Write($pngData)
$writer.Flush()

[System.IO.File]::WriteAllBytes($destPath, $ms.ToArray())

$writer.Dispose()
$ms.Dispose()
$pngStream.Dispose()
$bmp.Dispose()
$img.Dispose()

Write-Host "ICO file created successfully at: $destPath"

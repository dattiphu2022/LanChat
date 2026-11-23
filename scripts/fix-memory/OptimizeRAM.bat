@echo off
REM Script tự động áp dụng các tối ưu RAM cho LanChat
REM Chạy file này trước khi khởi động ứng dụng để tránh lỗi "Configuration system failed to initialize"

echo ================================================
echo   Tối ưu RAM cho LanChat v1.2.10
echo ================================================
echo.

REM Kiểm tra phiên bản hiện tại
if exist "%USERPROFILE%\AppData\Roaming\LanChat\LanChat.exe" (
    echo Đã tìm thấy LanChat.exe cũ tại: %USERPROFILE%\AppData\Roaming\LanChat
) else if exist "artifacts\LanChat-Win11-x64\LanChat.exe" (
    echo Đã tìm thấy LanChat.exe mới tại: artifacts\LanChat-Win11-x64
) else (
    echo Không tìm thấy file LanChat.exe!
    pause
    exit /b 1
)

REM Kiểm tra cấu hình App.config
echo Kiểm tra cấu hình App.config...
if not exist "src\LanChat.WinForms\App.config" (
    echo Đã copy App.config tối ưu vào thư mục src\LanChat.WinForms
    copy "artifacts\LanChat-Win11-x64\app.exe.config" "src\LanChat.WinForms\App.config" >nul 2>&1
) else if exist "artifacts\LanChat-Win11-x64\app.exe.config" (
    echo Đã copy App.config tối ưu vào thư mục src\LanChat.WinForms
    copy "artifacts\LanChat-Win11-x64\app.exe.config" "src\LanChat.WinForms\App.config" >nul 2>&1
)

REM Copy file LanChat.exe mới đã được tối ưu RAM
echo Copy file LanChat.exe tối ưu RAM vào thư mục dữ liệu...
if not exist "%USERPROFILE%\AppData\Roaming\LanChat" (
    mkdir "%USERPROFILE%\AppData\Roaming\LanChat" 2>nul
)

if exist "artifacts\LanChat-Win11-x64\LanChat.exe" (
    copy /Y "artifacts\LanChat-Win11-x64\LanChat.exe" "%USERPROFILE%\AppData\Roaming\LanChat\LanChat.exe" >nul 2>&1
    echo Đã cập nhật LanChat.exe!
)

echo.
echo ================================================
echo   Hoàn tất tối ưu RAM!
echo ================================================
echo.
echo Các cải tiến:
echo - Giới hạn cache thumbnail (tối đa 50 ảnh)
echo - Tối ưu bộ nhớ GC .NET Framework
echo - Thêm delay để tránh chiếm CPU cao
echo.
echo Khởi động LanChat qua lệnh: "LanChat.exe"
pause
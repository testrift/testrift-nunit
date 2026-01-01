@echo off
echo TestRift.NUnit - NUnit Test Runner
echo ================================

REM Set environment variables
set TEST_LOG_SERVER_URL=http://localhost:8080
set TEST_DUT_NAME=LENOVO-LAPTOP

REM Set user metadata environment variables for TRNUnitConfig.yaml
set DUT_MODEL=TestDevice-001
set DUT_URL=https://device-manager.example.com/devices/TestDevice-001
set FIRMWARE_BRANCH=release/v2.1.0
set TESTSYSTEM_URL=https://nuts.example.com/dashboard
set PRODUCT=SampleProduct
set BRANCH=release/v2.1.0

REM Set config path for user metadata
set TRNUNIT_CONFIG_PATH=TRNUnitConfig.yaml

echo Environment variables set:
echo   TEST_LOG_SERVER_URL=%TEST_LOG_SERVER_URL%
echo   TEST_DUT_NAME=%TEST_DUT_NAME%
echo   DUT_MODEL=%DUT_MODEL%
echo   DUT_URL=%DUT_URL%
echo   FIRMWARE_BRANCH=%FIRMWARE_BRANCH%
echo   TESTSYSTEM_URL=%TESTSYSTEM_URL%
echo   PRODUCT=%PRODUCT%
echo   BRANCH=%BRANCH%
echo   TRNUNIT_CONFIG_PATH=%TRNUNIT_CONFIG_PATH%
echo.

echo Building TestRift.NUnit library...
cd src\TestRift.NUnit
dotnet build
if %ERRORLEVEL% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo Building example tests...
cd ..\..\Example
dotnet build
if %ERRORLEVEL% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo Running example tests...
echo Make sure the Test Log Server is running on %TEST_LOG_SERVER_URL%
echo.

REM Run the example tests
dotnet test --logger "console;verbosity=normal"

echo.
echo Tests completed. Check the Test Log Server for results.
echo Log files are also saved locally in the bin directory

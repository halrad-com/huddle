@echo off
dotnet publish "%~dp0src\huddle.csproj" -c Release -o "%~dp0publish" %*

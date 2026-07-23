#!/bin/sh

rm -rf ~/.nuget/packages/dotkt*
dotnet nuget locals http-cache --clear

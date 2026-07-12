// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class LaunchCommand : InvokableCommand
{
    private readonly HuddleSettings _settings;

    public LaunchCommand(HuddleSettings settings)
    {
        _settings = settings;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Name = "Launch";
    }

    public override CommandResult Invoke()
    {
        var cmd = _settings.LaunchCommand;
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return CommandResult.KeepOpen();
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" {cmd}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            return CommandResult.Dismiss();
        }
        catch (Exception)
        {
            return CommandResult.KeepOpen();
        }
    }
}

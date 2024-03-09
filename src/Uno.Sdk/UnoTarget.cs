﻿using System.Diagnostics.CodeAnalysis;

namespace Uno.Sdk;

internal static class UnoTarget
{
	public const string Windows = "windows10";
	public const string Wasm = "browserwasm";
	public const string Android = "android";
	[SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "iOS is the correct style.")]
	public const string iOS = "ios";
	public const string MacCatalyst = "maccatalyst";
	public const string MacOS = "macos";
	public const string SkiaDesktop = "desktop";
}

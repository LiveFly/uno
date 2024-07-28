﻿#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarfBuzzSharp;
using SkiaSharp;
using Uno.Foundation.Logging;
using Microsoft.UI.Xaml.Documents.TextFormatting;
using Uno.Extensions;
using Buffer = HarfBuzzSharp.Buffer;
using GlyphInfo = Microsoft.UI.Xaml.Documents.TextFormatting.GlyphInfo;

using SegmentInfo = (int LeadingSpaces, int TrailingSpaces, int LineBreakLength, SkiaSharp.SKTypeface? Typeface, int NextStartingIndex);

namespace Microsoft.UI.Xaml.Documents
{
	partial class Run
	{
		private List<Segment>? _segments;

		internal IReadOnlyList<Segment> Segments => _segments ??= GetSegments();

		private static (int CodePoint, int Length) GetCodePoint(ReadOnlySpan<char> text, int i)
		{
			if (i + 1 < text.Length &&
				char.IsSurrogate(text[i]) &&
				char.IsSurrogatePair(text[i], text[i + 1]))
			{
				var codepoint = (int)((text[i] - 0xD800) * 0x400 + (text[i + 1] - 0xDC00) + 0x10000);
				return (codepoint, 2);
			}

			return (text[i], 1);
		}

		private SegmentInfo GetSegmentStartingFrom(int i, ReadOnlySpan<char> text)
		{
			int leadingSpaces = 0;
			int trailingSpaces = 0;
			int lineBreakLength = 0;
			var fontInfo = FontInfo;
			SKTypeface? segmentTypeface = null;

			// Count leading spaces
			while (i < text.Length && char.IsWhiteSpace(text[i]) && !Unicode.IsLineBreak(text[i]) && text[i] != '\t')
			{
				leadingSpaces++;
				i++;
			}

			// Keep the segment going until we hit a word break opportunity or a line break
			while (i < text.Length)
			{
				if (ProcessLineBreak(text, ref i, ref lineBreakLength))
				{
					break;
				}

				// Since tabs require special handling, we put tabs in separate segments.
				// Also, we don't consider tabs "spaces" since they don't get the general space treatment.
				if (text[i] == '\t')
				{
					i++;
					break;
				}

				if (i + 1 < text.Length && text[i + 1] == '\t')
				{
					if (char.IsWhiteSpace(text[i]))
					{
						trailingSpaces++;
					}

					i++;
					break;
				}

				if (Unicode.HasWordBreakOpportunityAfter(text, i) || (i + 1 < text.Length && Unicode.HasWordBreakOpportunityBefore(text, i + 1)))
				{
					if (char.IsWhiteSpace(text[i]))
					{
						trailingSpaces++;
					}

					i++;
					break;
				}

				var (codepoint, codepointLength) = GetCodePoint(text, i);

				var currentTypeface = fontInfo.SKFont.ContainsGlyph(codepoint)
					? fontInfo.SKFont.Typeface
					: SKFontManager.Default.MatchCharacter(codepoint);

				if (currentTypeface is null)
				{
					// The requested glyph isn't found by the OS.
					if (this.Log().IsEnabled(LogLevel.Trace))
					{
						this.Log().Trace($"Failed to match codepoint '{codepoint}' (length: {codepointLength}).");
					}

					// Move over the current codepoint.
					i += codepointLength;
				}
				else if (segmentTypeface is null || currentTypeface == segmentTypeface)
				{
					segmentTypeface = currentTypeface;
					i += codepointLength;
				}
				else
				{
					// Always break the current segment if the previous typeface and the current typeface are both non-null
					// and are different.
					break;
				}
			}

			// Tack on any trailing spaces or line breaks if this segment does not yet end in a line break
			if (lineBreakLength == 0)
			{
				while (i < text.Length)
				{
					if (ProcessLineBreak(text, ref i, ref lineBreakLength))
					{
						break;
					}

					if (char.IsWhiteSpace(text[i]) && text[i] != '\t')
					{
						trailingSpaces++;
						i++;
					}
					else
					{
						break;
					}
				}
			}

			return (leadingSpaces, trailingSpaces, lineBreakLength, segmentTypeface, i);
		}

		private List<Segment> GetSegments()
		{
			// TODO: Implement Bidi algorithm here to split segments by direction prior to doing the below processing on each directional piece.
			// TODO: Implement fallback font for international char segments

			List<Segment> segments = new();
			using HarfBuzzSharp.Buffer buffer = new();
			var fontInfo = FontInfo;
			var defaultTypeface = fontInfo.SKFont.Typeface;
			var defaultFont = fontInfo.Font;
			var paint = Paint;
			var fontSize = fontInfo.SKFontSize;

			defaultFont.GetScale(out int defaultFontScale, out _);
			float defaultTextSizeY = fontInfo.SKFontSize / defaultFontScale;
			float defaultTextSizeX = defaultTextSizeY * fontInfo.SKFontScaleX;

			var text = Text.AsSpan();
			int i = 0;

			while (i < text.Length)
			{
				var (leadingSpaces, trailingSpaces, lineBreakLength, typeface, nextStartingIndex) = GetSegmentStartingFrom(i, text);

				int length = nextStartingIndex - i;
				FontDetails? fallbackFont = null;
				Font font;
				int fontScale;
				float textSizeY;
				float textSizeX;
				if (typeface is not null && typeface != defaultTypeface)
				{
					fallbackFont = FontDetailsCache.GetFont(typeface.FamilyName, (float)FontSize, FontWeight, FontStretch, FontStyle);
					if (fallbackFont.CanChange)
					{
						fallbackFont.RegisterElementForFontLoaded(this);
					}

					font = fallbackFont.Font;
					font.GetScale(out fontScale, out _);
					textSizeY = fontSize / fontScale;
					textSizeX = textSizeY * fontInfo.SKFontScaleX;
				}
				else
				{
					font = defaultFont;
					fontScale = defaultFontScale;
					textSizeY = defaultTextSizeY;
					textSizeX = defaultTextSizeX;
				}

				if (length > 0)
				{
					if (lineBreakLength == 2)
					{
						buffer.AddUtf16(text.Slice(i, length - 1)); // Skip second line break char so that it is considered part of the same cluster as the first
					}
					else
					{
						buffer.AddUtf16(text.Slice(i, length));
					}

					// TODO: Set the segment properties instead of using HB guessing like below.
					// - Set direction using Bidi algorithm.
					// - Set Language and Script on buffer. From HarfBuzz docs:

					// Script is crucial for choosing the proper shaping behaviour for scripts that require it (e.g. Arabic) and the which OpenType features defined
					// in the font to be applied.

					// Languages are crucial for selecting which OpenType feature to apply to the buffer which can result in applying language-specific behaviour.
					// Languages are orthogonal to the scripts, and though they are related, they are different concepts and should not be confused with each other.

					// buffer.Direction = ...
					// buffer.Language = ...
					// buffer.Script = ...

					// Guess the above properties for now before shaping:
					buffer.GuessSegmentProperties();
					var direction = buffer.Direction == Direction.LeftToRight ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;

					// We don't support ligatures for now since they can cause buggy behaviour in TextBox
					// where multiple chars in a TextBox are turned into a single glyph.
					//https://github.com/unoplatform/uno/issues/15528
					// https://github.com/unoplatform/uno/issues/16788
					// https://harfbuzz.github.io/shaping-opentype-features.html
					font.Shape(buffer, new Feature(new Tag('l', 'i', 'g', 'a'), 0));

					if (buffer.Direction == Direction.RightToLeft)
					{
						buffer.ReverseClusters();
					}

					var glyphs = GetGlyphs(buffer, i, textSizeX, textSizeY);

					Debug.Assert(!(Text.AsSpan(i, length).Contains('\t')) || length == 1);
					if (length == 1 && text[i] == '\t')
					{
						glyphs[0] = glyphs[0] with { GlyphId = _getSpaceGlyph(fontInfo.Font) };
					}

					var segment = new Segment(this, direction, i, length, leadingSpaces, trailingSpaces, lineBreakLength, glyphs, fallbackFont);

					segments.Add(segment);
					buffer.ClearContents();
				}

				i = nextStartingIndex;
			}

			return segments;

			// Local functions:

			static List<GlyphInfo> GetGlyphs(Buffer buffer, int clusterStart, float textSizeX, float textSizeY)
			{
				int length = buffer.Length;
				var hbGlyphs = buffer.GetGlyphInfoSpan();
				var hbPositions = buffer.GetGlyphPositionSpan();

				List<TextFormatting.GlyphInfo> glyphs = new(length);

				for (int i = 0; i < length; i++)
				{
					var hbGlyph = hbGlyphs[i];
					var hbPos = hbPositions[i];

					// We add special handling for tabs, which don't get rendered correctly, and treated as an unknown glyph
					TextFormatting.GlyphInfo glyph = new(
						(ushort)hbGlyph.Codepoint,
						clusterStart + (int)hbGlyph.Cluster,
						hbPos.XAdvance * textSizeX,
						hbPos.XOffset * textSizeX,
						hbPos.YOffset * textSizeY
					);

					glyphs.Add(glyph);
				}

				return glyphs;
			}
		}

		private static bool ProcessLineBreak(ReadOnlySpan<char> text, ref int i, ref int lineBreakLength)
		{
			if (Unicode.IsLineBreak(text[i]))
			{
				if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
				{
					lineBreakLength = 2;
					i += 2;
				}
				else
				{
					lineBreakLength = 1;
					i++;
				}

				return true;
			}

			return false;
		}

		partial void InvalidateSegmentsPartial() => _segments = null;

		private static readonly Func<Font, ushort> _getSpaceGlyph =
			((Func<Font, ushort>?)(font =>
			{
				using var buffer = new HarfBuzzSharp.Buffer();
				buffer.AddUtf8(" ");
				buffer.GuessSegmentProperties();
				font.Shape(buffer);
				return (ushort)buffer.GlyphInfos[0].Codepoint;
			}))
			.AsMemoized();
	}
}

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.UI.Xaml.Controls.Primitives;

partial class Selector
{
	internal void SetAllowCustomValues(bool allow)
	{
		m_customValuesAllowed = allow;
	}
}

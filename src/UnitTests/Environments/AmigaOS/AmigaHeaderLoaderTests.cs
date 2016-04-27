﻿#region License
/* 
 * Copyright (C) 1999-2016 John Källén.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2, or (at your option)
 * any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; see the file COPYING.  If not, write to
 * the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA.
 */
#endregion

using NUnit.Framework;
using Reko.Arch.M68k;
using Reko.Core;
using Reko.Environments.AmigaOS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Reko.UnitTests.Environments.AmigaOS
{
    [TestFixture]
    public class AmigaHeaderLoaderTests
    {
        [Test]
        public void Ahl_ParseSimpleDef()
        {
            var arch = new M68kArchitecture();
            var platform = new AmigaOSPlatform(null, arch);
            var ahl = new AmigaHeaderLoader(null, "", Encoding.UTF8.GetBytes(
                "[[reko::amiga_function_vector(ExecLibrary, -432)]] [[reko::returns(register,\"A0\")]] " +
                "void * FlobDevice([[reko::arg(register, \"A1\")]] struct Device * device);"));
            var Q = ahl.Load(platform, new TypeLibrary());
            Assert.AreEqual(1, ahl.SystemServices.Count);
        }
    }
}
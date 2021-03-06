﻿/*
 * MindTouch Dream - a distributed REST framework 
 * Copyright (C) 2006-2015 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Text;
using NUnit.Framework;

namespace MindTouch.Dream.Test {
    
    [TestFixture]
    public class MimeTypeTests {

        [Test]
        public void MSDOCX() {
            var mimeType = MimeType.MSOFFICE_DOCX;
            Assert.IsNotNull(mimeType.CharSet);
        }

        [Test]
        public void Unsupported_charset_defaults_to_utf8_encoding() {
            var mimeType = new MimeType("application/vnd.openxmlformats-officedocument.wordprocessingml.document; charset=binary");
            Assert.AreEqual(Encoding.UTF8, mimeType.CharSet);
        }
    }
}
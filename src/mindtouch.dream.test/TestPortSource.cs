/*
 * MindTouch Dream - a distributed REST framework 
 * Copyright (C) 2006-2013 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit wiki.developer.mindtouch.com;
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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using log4net;

namespace MindTouch.Dream.Test {
    public static class TestPortSource {

        //--- Class Fields ---
        private static readonly ILog _log = LogUtils.CreateLog();
        private static readonly HashSet<int> _used = new HashSet<int>();
        private static readonly Random _random = new Random();
        private static int _rangeStart;
        private static int _rangeEnd;

        //--- Class Constructor ---
        static TestPortSource() {
            Initialize();
        }

        //--- Class Methods ---

        public static int GetAvailablePort() {
            lock(_used) {
                foreach(var conn in IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpConnections()
                    .Where(x => x.LocalEndPoint.AddressFamily == AddressFamily.InterNetwork)
                    .OrderBy(x => x.LocalEndPoint.ToString())
                    ) {
                    _used.Add(conn.LocalEndPoint.Port);
                }
                foreach(var listener in IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                    .OrderBy(x => x.Port)
                    ) {
                    _used.Add(listener.Port);
                }
                for(var i = 0; i < 5000; i++) {
                    var port = _random.Next(_rangeStart, _rangeEnd);
                    if(!_used.Contains(port)) {
                        _used.Add(port);
                        return port;
                    }
                }
                _log.DebugFormat("Ran out of ports, restarting in new range");
                Initialize();
                return GetAvailablePort();
            }
        }

        private static void Initialize() {
            _used.Clear();
            const int min = 2000;
            const int slices = 20;
            const int range = (65535 - min) / slices;
            var slice = _random.Next(0, slices);
            _rangeStart = min + (slice * range);
            _rangeEnd = _rangeStart + range;
            _log.DebugFormat("initialized process to port range {0} - {1}", _rangeStart, _rangeEnd);
        }
    }
}
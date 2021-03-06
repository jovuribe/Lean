/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;
using QuantConnect.Util;

namespace QuantConnect.ToolBox.AlgoSeekFuturesConverter
{
    /// <summary>
    /// Enumerator for converting AlgoSeek futures files into Ticks.
    /// </summary>
    public class AlgoSeekFuturesReader : IEnumerator<Tick>
    {
        private readonly Stream _stream;
        private readonly StreamReader _streamReader;
        private readonly HashSet<string> _symbolFilter;
        private readonly Dictionary<string, decimal> _symbolMultipliers;
        private readonly SymbolPropertiesDatabase _symbolProperties;

        private readonly int _columnTimestamp = -1;
        private readonly int _columnSecID = -1;
        private readonly int _columnTicker = -1;
        private readonly int _columnType = -1;
        private readonly int _columnSide = -1;
        private readonly int _columnQuantity = -1;
        private readonly int _columnPrice = -1;
        private readonly int _columnsCount = -1;

        /// <summary>
        /// Enumerate through the lines of the algoseek files.
        /// </summary>
        /// <param name="file">BZ File for AlgoSeek</param>
        /// <param name="symbolMultipliers">Symbol price multiplier</param>
        /// <param name="symbolFilter">Symbol filter to apply, if any</param>
        public AlgoSeekFuturesReader(string file, Dictionary<string, decimal> symbolMultipliers, HashSet<string> symbolFilter = null)
        {
            var streamProvider = StreamProvider.ForExtension(Path.GetExtension(file));
            _stream = streamProvider.Open(file).First();
            _streamReader = new StreamReader(_stream);
            _symbolFilter = symbolFilter;
            _symbolMultipliers = symbolMultipliers.ToDictionary();
            _symbolProperties = SymbolPropertiesDatabase.FromDataFolder();

            // detecting column order in the file
            var headerLine = _streamReader.ReadLine();
            if (!string.IsNullOrEmpty(headerLine))
            {
                var header = headerLine.ToCsv();
                _columnTimestamp = header.FindIndex(x => x == "Timestamp");
                _columnTicker = header.FindIndex(x => x == "Ticker");
                _columnType = header.FindIndex(x => x == "Type");
                _columnSide = header.FindIndex(x => x == "Side");
                _columnSecID = header.FindIndex(x => x == "SecurityID");
                _columnQuantity = header.FindIndex(x => x == "Quantity");
                _columnPrice = header.FindIndex(x => x == "Price");

                _columnsCount = new[] { _columnTimestamp, _columnTicker, _columnType, _columnSide, _columnSecID, _columnQuantity, _columnPrice }.Max();
            }
            //Prime the data pump, set the current.
            Current = null;
            MoveNext();
        }
        
        /// <summary>
        /// Parse the next line of the algoseek future file.
        /// </summary>
        /// <returns></returns>
        public bool MoveNext()
        {
            string line;
            Tick tick = null;
            while (tick == null && (line = _streamReader.ReadLine()) != null)
            {
                // If line is invalid continue looping to find next valid line.
                tick = Parse(line);
            }

            Current = tick;
            return Current != null;
        }

        /// <summary>
        /// Current top of the tick file.
        /// </summary>
        public Tick Current { get; private set; }

        /// <summary>
        /// Gets the current element in the collection.
        /// </summary>
        /// <returns>
        /// The current element in the collection.
        /// </returns>
        object IEnumerator.Current => Current;

        /// <summary>
        /// Reset the enumerator for the AlgoSeekFuturesReader
        /// </summary>
        public void Reset()
        {
            throw new NotImplementedException("Reset not implemented for AlgoSeekFuturesReader.");
        }

        /// <summary>
        /// Dispose of the underlying AlgoSeekFuturesReader
        /// </summary>
        public void Dispose()
        {
            _stream.Close();
            _stream.Dispose();
            _streamReader.Close();
            _streamReader.Dispose();
        }

        /// <summary>
        /// Parse a string line into a future tick.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private Tick Parse(string line)
        {
            try
            {
                const int TradeMask = 2;
                const int QuoteMask = 1;
                const int OpenInterestMask = 11;
                const int MessageTypeMask = 15;

                // parse csv check column count
                var csv = line.ToCsv();
                if (csv.Count - 1 < _columnsCount)
                {
                    return null;
                }

                var ticker = csv[_columnTicker];

                // we filter out options and spreads
                if (ticker.IndexOfAny(new [] { ' ', '-' }) != -1)
                {
                    return null;
                }

                ticker = ticker.Trim('"');

                if (string.IsNullOrEmpty(ticker))
                {
                    return null;
                }

                var symbol = SymbolRepresentation.ParseFutureSymbol(ticker);

                if (symbol == null || !_symbolMultipliers.ContainsKey(symbol.ID.Symbol) ||
                    _symbolFilter != null && !_symbolFilter.Contains(symbol.ID.Symbol, StringComparer.InvariantCultureIgnoreCase))
                {
                    return null;
                }

                // ignoring time zones completely -- this is all in the 'data-time-zone'
                var timeString = csv[_columnTimestamp];
                var time = DateTime.ParseExact(timeString, "yyyyMMddHHmmssFFF", CultureInfo.InvariantCulture);

                // detecting tick type (trade or quote)
                TickType tickType;
                bool isAsk = false;

                var type = csv[_columnType].ConvertInvariant<int>();
                if ((type & MessageTypeMask) == TradeMask)
                {
                    tickType = TickType.Trade;
                }
                else if ((type & MessageTypeMask) == OpenInterestMask)
                {
                    tickType = TickType.OpenInterest;
                }
                else if ((type & MessageTypeMask) == QuoteMask)
                {
                    tickType = TickType.Quote;

                    switch (csv[_columnSide])
                    {
                        case "B":
                            isAsk = false;
                            break;
                        case "S":
                            isAsk = true;
                            break;
                        default:
                        {
                            return null;
                        }
                    }
                }
                else
                {
                    return null;
                }

                // All futures but VIX are delivered with a scale factor of 10000000000.
                var scaleFactor = symbol.ID.Symbol == "VX" ? decimal.One : 10000000000m;

                var price = csv[_columnPrice].ToDecimal() / scaleFactor;
                var quantity = csv[_columnQuantity].ToInt32();

                price *= _symbolMultipliers[symbol.ID.Symbol];

                switch (tickType)
                {
                    case TickType.Quote:

                        var tick = new Tick
                        {
                            Symbol = symbol,
                            Time = time,
                            TickType = tickType,
                            Value = price
                        };

                        if (isAsk)
                        {
                            tick.AskPrice = price;
                            tick.AskSize = quantity;
                        }
                        else
                        {
                            tick.BidPrice = price;
                            tick.BidSize = quantity;
                        }

                        return tick;

                    case TickType.Trade:

                        tick = new Tick
                        {
                            Symbol = symbol,
                            Time = time,
                            TickType = tickType,
                            Value = price,
                            Quantity = quantity
                        };
                        return tick;

                    case TickType.OpenInterest:

                        tick = new Tick
                        {
                            Symbol = symbol,
                            Time = time,
                            TickType = tickType,
                            Exchange = symbol.ID.Market,
                            Value = quantity
                        };
                        return tick;
                }

                return null;
            }
            catch (Exception err)
            {
                Log.Error(err);
                Log.Trace("Line: {0}", line);
                return null;
            }
        }
    }
}

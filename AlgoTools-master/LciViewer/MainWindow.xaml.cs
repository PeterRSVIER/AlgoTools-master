﻿namespace LciViewer
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Threading.Tasks;
	using System.Windows;
	using System.Windows.Controls;
	using System.Windows.Media;

	using Ecng.Collections;
	using Ecng.Common;
	using Ecng.Interop;
	using Ecng.Serialization;
	using Ecng.Xaml;

	using MoreLinq;

	using StockSharp.Algo;
	using StockSharp.Algo.Candles;
	using StockSharp.Algo.History.Russian.Finam;
	using StockSharp.Algo.History.Russian.Rts;
	using StockSharp.Algo.Indicators;
	using StockSharp.Algo.PnL;
	using StockSharp.Algo.Statistics;
	using StockSharp.Algo.Storages;
	using StockSharp.BusinessEntities;
	using StockSharp.Localization;
	using StockSharp.Messages;
	using StockSharp.Xaml;
	using StockSharp.Xaml.Charting;
	using StockSharp.Xaml.Charting.IndicatorPainters;

	public partial class MainWindow
	{
		class SecurityStorage : ISecurityStorage
		{
			private readonly Dictionary<string, List<Security>> _securitiesByCode = new Dictionary<string, List<Security>>(StringComparer.InvariantCultureIgnoreCase);
			private readonly Dictionary<string, Security> _securitiesById = new Dictionary<string, Security>(StringComparer.InvariantCultureIgnoreCase);

			IEnumerable<Security> ISecurityProvider.Lookup(Security criteria)
			{
				if (criteria.Code == "*")
					return _securitiesById.Values;

				var security = _securitiesById.TryGetValue(criteria.Id);
				
				if (security != null)
					return new[] { security };

				return _securitiesByCode.TryGetValue(criteria.Code) ?? Enumerable.Empty<Security>();
			}

			object ISecurityProvider.GetNativeId(Security security)
			{
				throw new NotSupportedException();
			}

			void ISecurityStorage.Save(Security security)
			{
				_securitiesByCode.SafeAdd(security.Code).Add(security);
				_securitiesById[security.Id] = security;
			}

			IEnumerable<string> ISecurityStorage.GetSecurityIds()
			{
				return _securitiesById.Keys;
			}

			public event Action<Security> NewSecurity;

			void ISecurityStorage.Delete(Security security)
			{
				throw new NotSupportedException();
			}

			void ISecurityStorage.DeleteBy(Security criteria)
			{
				throw new NotSupportedException();
			}
		}

		class DatesCache
		{
			private readonly SynchronizedOrderedList<DateTime> _dates = new SynchronizedOrderedList<DateTime>();

			private readonly string _filePath;

			public DateTime? MinValue { get { return _dates.FirstOr(); } }

			public DateTime? MaxValue { get { return _dates.LastOr(); } }

			public DatesCache(string filePath)
			{
				_filePath = filePath;

				if (File.Exists(_filePath))
					CultureInfo.InvariantCulture.DoInCulture(() => _dates.AddRange(new XmlSerializer<DateTime[]>().Deserialize(_filePath)));
			}

			public void Add(params DateTime[] dates)
			{
				if (dates == null)
					throw new ArgumentNullException("dates");

				_dates.AddRange(dates.Where(d => d < DateTime.Today));

				_filePath.CreateDirIfNotExists();
				CultureInfo.InvariantCulture.DoInCulture(() => new XmlSerializer<DateTime[]>().Serialize(_dates.ToArray(), _filePath));
			}

			public bool Contains(DateTime date)
			{
				return _dates.Contains(date);
			}
		}

		class PnlPainter : BaseChartIndicatorPainter
		{
			private ChartIndicatorElement _pnl;

			public override IEnumerable<ChartIndicatorElement> Init()
			{
				InnerElements.Clear();

				InnerElements.Add(_pnl = new ChartIndicatorElement
				{
					YAxisId = BaseElement.YAxisId,
					DrawStyle = ChartIndicatorDrawStyles.BandOneValue,
					Color = Colors.Green,
					AdditionalColor = Colors.Red,
					StrokeThickness = BaseElement.StrokeThickness,
					Title = LocalizedStrings.PnL
				});

				return InnerElements;
			}

			public override IEnumerable<decimal> ProcessValues(DateTimeOffset time, IIndicatorValue value, DrawHandler draw)
			{
				var newYValues = new List<decimal>();

				if (!value.IsFormed)
				{
					draw(_pnl, 0, double.NaN, double.NaN);
				}
				else
				{
					var pnl = value.GetValue<decimal>();

					draw(_pnl, 0, (double)pnl, (double)0);
					newYValues.Add(pnl);
				}

				return newYValues;
			}
		}

		private class ColorSettings
		{
			public Color Position { get; set; }
			public Color Buy { get; set; }
			public Color Sell { get; set; }
		}

		private readonly FinamHistorySource _finamHistorySource = new FinamHistorySource();
		private readonly ISecurityStorage _securityStorage = new SecurityStorage();
		private readonly StorageRegistry _dataRegistry = new StorageRegistry { DefaultDrive = new LocalMarketDataDrive(Path.Combine(_settingsDir, "Data")) };
		private readonly Dictionary<string, StorageRegistry> _traderStorages = new Dictionary<string, StorageRegistry>(StringComparer.InvariantCultureIgnoreCase);
		private readonly Competition _competition = new Competition();
		private readonly StatisticManager _statisticManager = new StatisticManager();

		private readonly Dictionary<string, DatesCache> _tradesDates = new Dictionary<string, DatesCache>();
		private readonly Dictionary<Tuple<Security, TimeSpan>, DatesCache> _candlesDates = new Dictionary<Tuple<Security, TimeSpan>, DatesCache>();

		private readonly Dictionary<Security, List<Candle>> _candles = new Dictionary<Security, List<Candle>>();
		private readonly FilterableSecurityProvider _securityProvider;
		private readonly Dictionary<SecurityEditor, ColorSettings> _securityCtrls = new Dictionary<SecurityEditor, ColorSettings>();

		private const string _settingsDir = "Settings";

		private class Settings : IPersistable
		{
			public DateTime Year { get; set; }
			public string Trader { get; set; }
			public DateTime? From { get; set; }
			public DateTime? To { get; set; }
			public string Security1 { get; set; }
			public string Security2 { get; set; }
			public string Security3 { get; set; }
			public string Security4 { get; set; }
			public bool Apart { get; set; }
			public TimeSpan TimeFrame { get; set; }

			void IPersistable.Load(SettingsStorage storage)
			{
				Year = storage.GetValue<DateTime>("Year");
				Trader = storage.GetValue<string>("Trader");
				From = storage.GetValue<DateTime?>("From");
				To = storage.GetValue<DateTime?>("To");
				Security1 = storage.GetValue<string>("Security1");
				Security2 = storage.GetValue<string>("Security2");
				Security3 = storage.GetValue<string>("Security3");
				Security4 = storage.GetValue<string>("Security4");
				Apart = storage.GetValue("Apart", true);
				TimeFrame = storage.GetValue<TimeSpan>("TimeFrame");
			}

			void IPersistable.Save(SettingsStorage storage)
			{
				storage.SetValue("Year", Year);
				storage.SetValue("Trader", Trader);
				
				if (From != null)
					storage.SetValue("From", From);

				if (To != null)
					storage.SetValue("To", To);

				storage.SetValue("Security1", Security1);
				storage.SetValue("Security2", Security2);
				storage.SetValue("Security3", Security3);
				storage.SetValue("Security4", Security4);
				storage.SetValue("Apart", Apart);
				storage.SetValue("TimeFrame", TimeFrame);
			}
		}

		private static readonly string _settingsFile = Path.Combine(_settingsDir, "setting.xml");

		public MainWindow()
		{
			InitializeComponent();

			_securityCtrls.Add(Security1, new ColorSettings
			{
				Position = Colors.Green,
				Buy = Colors.Green,
				Sell = Colors.Red,
			});
			_securityCtrls.Add(Security2, new ColorSettings
			{
				Position = Colors.Blue,
				Buy = Colors.Teal,
				Sell = Colors.BlueViolet,
			});
			_securityCtrls.Add(Security3, new ColorSettings
			{
				Position = Colors.Brown,
				Buy = Colors.Yellow,
				Sell = Colors.Brown,
			});
			_securityCtrls.Add(Security4, new ColorSettings
			{
				Position = Colors.YellowGreen,
				Buy = Colors.Cyan,
				Sell = Colors.DeepPink,
			});

			Chart.IsInteracted = true;
			//Chart.IsAutoRange = true;

			Chart.SubscribeIndicatorElement += Chart_SubscribeIndicatorElement;

			_securityProvider = new FilterableSecurityProvider();
			_securityCtrls.ForEach(pair => pair.Key.SecurityProvider = _securityProvider);

			TimeFrame.ItemsSource = new[] { TimeSpan.FromTicks(1) }.Concat(FinamHistorySource.TimeFrames);
			TimeFrame.SelectedItem = TimeSpan.FromMinutes(5);

			Statistics.StatisticManager = _statisticManager;
		}

		private void Chart_SubscribeIndicatorElement(ChartIndicatorElement element, CandleSeries series, IIndicator indicator)
		{
			var candles = _candles.TryGetValue(series.Security);

			if (candles == null)
				throw new InvalidOperationException("_candles == null");

			var values = candles
				.Select(candle =>
				{
					if (candle.State != CandleStates.Finished)
						candle.State = CandleStates.Finished;

					return new RefPair<DateTimeOffset, IDictionary<IChartElement, object>>(candle.OpenTime, new Dictionary<IChartElement, object>
					{
						{ element, indicator.Process(candle) }
					});
				})
				.ToArray();

			Chart.Draw(values);
		}

		private Competition.CompetitionYear SelectedYear
		{
			get { return _competition.Get((DateTime)Year.SelectedItem); }
		}

		private string SelectedTrader
		{
			get { return (string)Trader.SelectedItem; }
		}

		private TimeSpan SelectedTimeFrame
		{
			get { return (TimeSpan)TimeFrame.SelectedItem; }
		}

		private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
		{
			Year.ItemsSource = Competition.AllYears;
			Year.SelectedItem = Competition.AllYears.Last();

			Directory.CreateDirectory(_settingsDir);

			var ns = typeof(IIndicator).Namespace;

			var rendererTypes = typeof(Chart).Assembly
				.GetTypes()
				.Where(t => !t.IsAbstract && typeof(BaseChartIndicatorPainter).IsAssignableFrom(t))
				.ToDictionary(t => t.Name);

			var indicators = typeof(IIndicator).Assembly
				.GetTypes()
				.Where(t => t.Namespace == ns && !t.IsAbstract && typeof(IIndicator).IsAssignableFrom(t))
				.Select(t => new IndicatorType(t, rendererTypes.TryGetValue(t.Name + "Painter")));

			Chart.IndicatorTypes.AddRange(indicators);

			var finamSecurities = Path.Combine(_settingsDir, "finam2.csv");

			BusyIndicator.BusyContent = "Обновление инструментов...";
			BusyIndicator.IsBusy = true;

			Task.Factory.StartNew(() =>
			{
				File.Delete("finam.csv");

				if (File.Exists(finamSecurities))
				{
					CultureInfo.InvariantCulture.DoInCulture(() =>
					{
						var idGen = new SecurityIdGenerator();

						var securities = File.ReadAllLines(finamSecurities).Select(line =>
						{
							var cells = line.SplitByComma();
							var idParts = idGen.Split(cells[0]);

							return new Security
							{
								Id = cells[0],
								Code = idParts.Item1,
								Board = ExchangeBoard.GetOrCreateBoard(idParts.Item2),
								ExtensionInfo = new Dictionary<object, object>
								{
									{ FinamHistorySource.MarketIdField, cells[1].To<long>() },
									{ FinamHistorySource.SecurityIdField, cells[2].To<long>() },
								},
								PriceStep = cells[3].To<decimal?>(),
								Decimals = cells[4].To<int?>(),
								Currency = cells[5].To<CurrencyTypes?>(),
							};
						});

						foreach (var security in securities)
						{
							_securityProvider.Securities.Add(security);
							_securityStorage.Save(security);
						}
					});
				}
				else
				{
					_finamHistorySource.Refresh(_securityStorage, new Security(), s => { }, () => false);

					var securities = _securityStorage.LookupAll().ToArray();

					foreach (var security in securities)
						_securityProvider.Securities.Add(security);

					File.WriteAllLines(finamSecurities, securities.Where(s => !s.Id.Contains(',')).Select(s => "{0},{1},{2},{3},{4},{5}"
						.Put(s.Id, s.ExtensionInfo[FinamHistorySource.MarketIdField], s.ExtensionInfo[FinamHistorySource.SecurityIdField], s.PriceStep, s.Decimals, s.Currency)));
				}
			})
			.ContinueWith(res =>
			{
				BusyIndicator.IsBusy = false;

				if (res.Exception != null)
				{
					new MessageBoxBuilder()
						.Error()
						.Owner(this)
						.Text(res.Exception.ToString())
						.Show();
				}

				if (File.Exists(_settingsFile))
				{
					var settings = CultureInfo.InvariantCulture.DoInCulture(() => new XmlSerializer<SettingsStorage>().Deserialize(_settingsFile).Load<Settings>());

					Year.SelectedItem = settings.Year;
					Trader.Text = settings.Trader;
					From.Value = settings.From;
					To.Value = settings.To;
					Security1.Text = settings.Security1;
					Security2.Text = settings.Security2;
					Security3.Text = settings.Security3;
					Security4.Text = settings.Security4;
					TimeFrame.SelectedItem = settings.TimeFrame;
					Apart.IsChecked = settings.Apart;
				}
				else
				{
					Trader.Text = "Vasya";
					Security1.Text = "RIZ5@FORTS";
					//Trader.Text = "iZotov";
					//Security1.Text = "SPZ5@FORTS";
					//Security2.Text = "SIZ5@FORTS";
					//From.Value = new DateTime(2014, 09, 16);
					Apart.IsChecked = true;
				}
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}

		private void Year_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			From.Value = To.Value = null;

			From.Minimum = To.Minimum = SelectedYear.Days.First();
			From.Maximum = To.Maximum = SelectedYear.Days.Last();

			if (SelectedYear.Year.Year == DateTime.Today.Year)
			{
				From.Value = DateTime.Today.Min(From.Maximum.Value).Subtract(TimeSpan.FromDays(7)).Max(From.Maximum.Value);
			}

			Trader.ItemsSource = SelectedYear.Members;
			Trader.SelectedIndex = 0;
		}

		private void Trader_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			TryEnableDownload();
		}

		private void OnSecuritySelected()
		{
			TryEnableDownload();
		}

		private void TryEnableDownload()
		{
			Download.IsEnabled = SelectedTrader != null && _securityCtrls.Any(pair => pair.Key.SelectedSecurity != null);
		}
		
		private void Download_OnClick(object sender, RoutedEventArgs e)
		{
			var settings = new Settings
			{
				Year = SelectedYear.Year,
				Trader = Trader.Text,
				From = From.Value,
				To = To.Value,
				Security1 = Security1.Text,
				Security2 = Security2.Text,
				Security3 = Security3.Text,
				Security4 = Security4.Text,
				TimeFrame = SelectedTimeFrame,
				Apart = Apart.IsChecked == true,
			};
			CultureInfo.InvariantCulture.DoInCulture(() => new XmlSerializer<SettingsStorage>().Serialize(settings.Save(), _settingsFile));

			var year = SelectedYear;
			var from = From.Value ?? year.Days.First();
			var to = (To.Value ?? year.Days.Last()).EndOfDay();
			var trader = SelectedTrader;
			var tf = SelectedTimeFrame;
			var apart = Apart.IsChecked == true;
			
			var seriesSet = _securityCtrls
				.Where(pair => pair.Key.SelectedSecurity != null)
				.Select(pair => Tuple.Create(new CandleSeries(typeof(TimeFrameCandle), pair.Key.SelectedSecurity, tf), pair.Value))
				.ToArray();

			BusyIndicator.BusyContent = "Подготовка данных...";
			BusyIndicator.IsBusy = true;

			_candles.Clear();

			var trades = new Dictionary<Security, Dictionary<DateTimeOffset, Tuple<MyTrade[], MyTrade>>>();

			var worker = new BackgroundWorker { WorkerReportsProgress = true };

			worker.DoWork += (o, ea) =>
			{
				foreach (var series in seriesSet)
				{
					var security = series.Item1.Security;
					var candleStorage = _dataRegistry.GetCandleStorage(series.Item1, format: StorageFormats.Csv);
					var secCandles = _candles.SafeAdd(security);
					
					secCandles.Clear();
					secCandles.AddRange(candleStorage.Load(from, to));

					var candlesDatesCache = _candlesDates.SafeAdd(Tuple.Create(security, tf), k => new DatesCache(Path.Combine(((LocalMarketDataDrive)candleStorage.Drive.Drive).GetSecurityPath(security.ToSecurityId()), "{0}min_date.bin".Put((int)tf.TotalMinutes))));

					var minCandleDate = candlesDatesCache.MinValue;
					var maxCandleDate = candlesDatesCache.MaxValue;

					if (from >= minCandleDate && to <= maxCandleDate)
						continue;

					var finamFrom = from;
					var finamTo = to;

					if (maxCandleDate != null && finamFrom >= minCandleDate && finamFrom <= maxCandleDate)
						finamFrom = maxCandleDate.Value + TimeSpan.FromDays(1);

					if (minCandleDate != null && finamTo >= minCandleDate && finamTo <= maxCandleDate)
						finamTo = minCandleDate.Value - TimeSpan.FromDays(1);

					if (finamTo <= finamFrom)
						continue;

					TimeFrameCandle[] newCandles;

					if (tf.Ticks == 1)
					{
						newCandles = finamFrom.Range(finamTo, TimeSpan.FromDays(1)).SelectMany(day =>
						{
							worker.ReportProgress(1, Tuple.Create(security, day));

							var candles = _finamHistorySource.GetTrades(security, day, day).ToEx().ToCandles<TimeFrameCandle>(tf).ToArray();
							candleStorage.Save(candles);
							candlesDatesCache.Add(day);
							return candles;
						}).ToArray();
					}
					else
					{
						worker.ReportProgress(1, Tuple.Create(security, finamFrom, finamTo));
						newCandles = _finamHistorySource.GetCandles(security, tf, finamFrom, finamTo).ToArray();
						
						candleStorage.Save(newCandles);
						candlesDatesCache.Add(newCandles.Select(c => c.OpenTime.Date).Distinct().ToArray());
					}

					// TODO
					secCandles.AddRange(newCandles);
				}

				var traderDrive = new LocalMarketDataDrive(Path.Combine(_settingsDir, trader));
				var traderStorage = _traderStorages.SafeAdd(trader, key => new StorageRegistry { DefaultDrive = traderDrive });

				foreach (var series in seriesSet)
				{
					var security = series.Item1.Security;

					var olStorage = traderStorage.GetOrderLogStorage(security, format: StorageFormats.Csv);
					var tradeDatesCache = _tradesDates.SafeAdd(trader, k => new DatesCache(Path.Combine(traderDrive.Path, "dates.xml")));

					var secTrades = from
						.Range(to, TimeSpan.FromDays(1))
						.Intersect(year.Days)
						.SelectMany(date =>
						{
							if (olStorage.Dates.Contains(date))
								return olStorage.Load(date);

							if (tradeDatesCache.Contains(date))
								return Enumerable.Empty<OrderLogItem>();

							worker.ReportProgress(2, date);

							var loadedTrades = year.GetTrades(_securityStorage, trader, date);

							var dateTrades = Enumerable.Empty<OrderLogItem>();

							foreach (var group in loadedTrades.GroupBy(t => t.Order.Security))
							{
								var sec = group.Key;

								traderStorage
									.GetOrderLogStorage(sec, format: StorageFormats.Csv)
									.Save(group.OrderBy(i => i.Order.Time));

								if (group.Key == security)
									dateTrades = group;
							}

							tradeDatesCache.Add(date);

							return dateTrades;
						})
						.GroupBy(ol =>
						{
							var time = ol.Order.Time;

							var period = security.Board.WorkingTime.GetPeriod(time.ToLocalTime(security.Board.Exchange.TimeZoneInfo));
							if (period != null && period.Times.Length > 0)
							{
								var last = period.Times.Last().Max;

								if (time.TimeOfDay >= last)
									time = time.AddTicks(-1);
							}

							if (tf == TimeSpan.FromDays(1) && period != null && period.Times.Length > 0)
							{
								return new DateTimeOffset(time.Date + period.Times[0].Min, time.Offset);
							}

							return time.Truncate(tf);
						})
						.ToDictionary(g => g.Key, g =>
						{
							var candleTrades = g.Select(ol => new MyTrade
							{
								Order = ol.Order,
								Trade = ol.Trade
							})
							.ToArray();

							if (candleTrades.Length == 0)
								return null;

							var order = candleTrades[0].Order;
							var volume = candleTrades.Sum(t1 => t1.Trade.Volume * (t1.Order.Direction == Sides.Buy ? 1 : -1));

							if (volume == 0)
								return Tuple.Create(candleTrades, (MyTrade)null);

							var side = volume > 0 ? Sides.Buy : Sides.Sell;

							volume = volume.Abs();

							var availableVolume = volume;
							var avgPrice = 0m;

							foreach (var trade in candleTrades.Where(t1 => t1.Order.Direction == side))
							{
								var tradeVol = trade.Trade.Volume.Min(availableVolume);
								avgPrice += trade.Trade.Price * tradeVol;

								availableVolume -= tradeVol;

								if (availableVolume <= 0)
									break;
							}

							avgPrice = avgPrice / volume;

							return Tuple.Create(candleTrades, new MyTrade
							{
								Order = new Order
								{
									Security = order.Security,
									Direction = side,
									Time = g.Key,
									Portfolio = order.Portfolio,
									Price = avgPrice,
									Volume = volume,
								},
								Trade = new Trade
								{
									Security = order.Security,
									Time = g.Key,
									Volume = volume,
									Price = avgPrice
								}
							});
						});

					trades.Add(security, secTrades);
				}
			};

			worker.ProgressChanged += (o, ea) =>
			{
				switch (ea.ProgressPercentage)
				{
					case 1:
					{
						if (ea.UserState is Tuple<Security, DateTime>)
							BusyIndicator.BusyContent = "Скачивание {Item1.Id} тиков за {Item2:yyyy-MM-dd}...".PutEx(ea.UserState);
						else
							BusyIndicator.BusyContent = "Скачивание {Item1.Id} свечей с {Item2:yyyy-MM-dd} по {Item3:yyyy-MM-dd}...".PutEx(ea.UserState);
						
						break;
					}

					default:
						BusyIndicator.BusyContent = "Скачивание сделок за {0:yyyy-MM-dd}...".Put(ea.UserState);
						break;
				}
			};

			worker.RunWorkerCompleted += (o, ea) =>
			{
				BusyIndicator.IsBusy = false;

				if (ea.Error == null)
				{
					Chart.ClearAreas();
					
					_statisticManager.Reset();

					var equityInd = new SimpleMovingAverage { Length = 1 };
					ChartIndicatorElement equityElem;
					var candlesAreas = new Dictionary<CandleSeries, ChartArea>();

					if (apart)
					{
						foreach (var series in seriesSet)
						{
							var area = new ChartArea { Title = series.Item1.Security.Id };
							Chart.AddArea(area);
							area.YAxises.Clear();
							candlesAreas.Add(series.Item1, area);
						}

						var equityArea = new ChartArea { Title = LocalizedStrings.PnL };
						Chart.AddArea(equityArea);

						equityElem = new ChartIndicatorElement
						{
							FullTitle = LocalizedStrings.PnL,
							IndicatorPainter = new PnlPainter()
						};
						Chart.AddElement(equityArea, equityElem);
					}
					else
					{
						var candlesArea = new ChartArea();
						Chart.AddArea(candlesArea);

						foreach (var tuple in seriesSet)
						{
							candlesAreas.Add(tuple.Item1, candlesArea);
						}

						const string equityYAxis = "Equity";

						candlesArea.YAxises.Clear();
						candlesArea.YAxises.Add(new ChartAxis
						{
							Id = equityYAxis,
							AutoRange = true,
							AxisType = ChartAxisType.Numeric,
							AxisAlignment = ChartAxisAlignment.Left,
						});
						equityElem = new ChartIndicatorElement
						{
							YAxisId = equityYAxis,
							FullTitle = LocalizedStrings.PnL,
							IndicatorPainter = new PnlPainter()
						};
						Chart.AddElement(candlesArea, equityElem);
					}

					var positionArea = new ChartArea { Height = 100 };
					Chart.AddArea(positionArea);
					positionArea.YAxises.Clear();

					var chartValues = new SortedDictionary<DateTimeOffset, IDictionary<IChartElement, object>>();
					var pnlValues = new Dictionary<DateTimeOffset, decimal>();

					foreach (var series in seriesSet)
					{
						var security = series.Item1.Security;

						var candleYAxis = "Candles_Y_" + security.Id;

						var candlesArea = candlesAreas[series.Item1];

						candlesArea.YAxises.Add(new ChartAxis
						{
							Id = candleYAxis,
							AutoRange = true,
							AxisType = ChartAxisType.Numeric,
							AxisAlignment = ChartAxisAlignment.Right,
						});

						var candlesElem = new ChartCandleElement
						{
							ShowAxisMarker = false,
							YAxisId = candleYAxis,
						};
						Chart.AddElement(candlesArea, candlesElem, series.Item1);

						var tradesElem = new ChartTradeElement
						{
							BuyStrokeColor = Colors.Black,
							SellStrokeColor = Colors.Black,
							BuyColor = series.Item2.Buy,
							SellColor = series.Item2.Sell,
							FullTitle = LocalizedStrings.Str985 + " " + security.Id,
							YAxisId = candleYAxis,
						};
						Chart.AddElement(candlesArea, tradesElem);

						var posYAxis = "Pos_Y_" + security.Id;
						positionArea.YAxises.Add(new ChartAxis
						{
							Id = posYAxis,
							AutoRange = true,
							AxisType = ChartAxisType.Numeric,
							AxisAlignment = ChartAxisAlignment.Right,
						});
						var positionElem = new ChartIndicatorElement
						{
							FullTitle = LocalizedStrings.Str862 + " " + security.Id,
							YAxisId = posYAxis,
							Color = series.Item2.Position
						};
						var positionInd = new SimpleMovingAverage { Length = 1 };
						Chart.AddElement(positionArea, positionElem);

						var pnlQueue = new PnLQueue(security.ToSecurityId());
						//var level1Info = new Level1ChangeMessage
						//{
						//	SecurityId = pnlQueue.SecurityId,
						//}
						//.TryAdd(Level1Fields.PriceStep, security.PriceStep)
						//.TryAdd(Level1Fields.StepPrice, security.StepPrice);

						//pnlQueue.ProcessLevel1(level1Info);

						var pos = 0m;

						var secTrades = trades[security];

						var secValues = _candles[security]
							.Select(c =>
							{
								if (c.State != CandleStates.Finished)
									c.State = CandleStates.Finished;

								pnlQueue.ProcessLevel1(new Level1ChangeMessage
								{
									SecurityId = security.ToSecurityId(),
								}.TryAdd(Level1Fields.LastTradePrice, c.ClosePrice));

								var values = new Dictionary<IChartElement, object>
								{
									{ candlesElem, c },
								};

								var candleTrade = secTrades.TryGetValue(c.OpenTime);

								if (candleTrade != null)
								{
									if (candleTrade.Item2 != null)
										values.Add(tradesElem, candleTrade.Item2);

									foreach (var myTrade in candleTrade.Item1)
									{
										pos += myTrade.Order.Direction == Sides.Buy ? myTrade.Trade.Volume : -myTrade.Trade.Volume;
										var pnl = pnlQueue.Process(myTrade.ToMessage());

										_statisticManager.AddMyTrade(pnl);
									}

									_statisticManager.AddPosition(c.OpenTime, pos);
									_statisticManager.AddPnL(c.OpenTime, pnlQueue.RealizedPnL + pnlQueue.UnrealizedPnL);
								}

								pnlValues[c.OpenTime] = pnlValues.TryGetValue(c.OpenTime) + (pnlQueue.RealizedPnL + pnlQueue.UnrealizedPnL);
								values.Add(positionElem, positionInd.Process(pos));

								return new RefPair<DateTimeOffset, IDictionary<IChartElement, object>>
								{
									First = c.OpenTime,
									Second = values
								};
							})
							.ToArray();

						foreach (var pair in secValues)
						{
							var dict = chartValues.SafeAdd(pair.First, key => new Dictionary<IChartElement, object>());

							foreach (var pair2 in pair.Second)
							{
								dict[pair2.Key] = pair2.Value;
							}
						}
					}

					foreach (var pair in pnlValues)
					{
						chartValues[pair.Key].Add(equityElem, equityInd.Process(pair.Value));
					}

					Chart.IsAutoRange = true;

					try
					{
						Chart.Draw(chartValues.Select(p => RefTuple.Create(p.Key, p.Value)));
					}
					finally
					{
						Chart.IsAutoRange = false;
					}
				}
				else
				{
					new MessageBoxBuilder()
						.Error()
						.Owner(this)
						.Text(ea.Error.ToString())
						.Show();
				}
			};

			worker.RunWorkerAsync();
		}
	}
}
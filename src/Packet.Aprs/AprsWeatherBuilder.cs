namespace Packet.Aprs;

/// <summary>
/// A weather report, from <see cref="AprsStation.Weather()"/> (APRS12c §12). With a position it is
/// a position report with the weather station symbol, as APRS12c recommends; without, a
/// positionless weather report. Values are in the units APRS sends (mph, Fahrenheit, inches,
/// millibars); the <c>Celsius</c>, <c>Metres</c> and similar methods convert.
/// </summary>
public sealed class AprsWeatherBuilder : AprsBuilder
{
    private AprsWeather weather = new();
    private AprsPosition? position;
    private AprsSymbol symbol = AprsSymbol.WeatherStation;
    private AprsTimestamp? timestamp;
    private DateTime? time;
    private bool messaging;
    private bool compressed;
    private AprsDao? dao;

    internal AprsWeatherBuilder(AprsStation station)
        : base(station)
    {
    }

    /// <summary>Where the weather station is, in decimal degrees: north and east positive.</summary>
    public AprsWeatherBuilder At(double latitude, double longitude) => At(new AprsPosition(latitude, longitude));

    /// <summary>Where the weather station is.</summary>
    public AprsWeatherBuilder At(AprsPosition position)
    {
        this.position = position;
        return this;
    }

    /// <summary>The symbol, when not the plain weather station <c>/_</c>; it must still be a weather
    /// station, e.g. <see cref="AprsSymbol.WeatherStationWithDigipeater"/>. Only with a position.</summary>
    public AprsWeatherBuilder Symbol(AprsSymbol symbol)
    {
        this.symbol = symbol;
        return this;
    }

    /// <summary>
    /// When the observations were made. Without this a report with a position is current and has no
    /// timestamp, and a positionless one gets the time of <c>ToData()</c> or <c>Build()</c>. A
    /// positionless report takes only a month-day timestamp (<c>MMDDHHMM</c>).
    /// </summary>
    public AprsWeatherBuilder Timestamp(AprsTimestamp timestamp)
    {
        this.timestamp = timestamp;
        time = null;
        return this;
    }

    /// <summary>When the observations were made, sent in the form the report takes: day, hours and
    /// minutes with a position, month, day, hours and minutes without.</summary>
    public AprsWeatherBuilder Timestamp(DateTime utc)
    {
        timestamp = null;
        time = utc;
        return this;
    }

    /// <summary>Says the station can receive APRS messages. Only with a position.</summary>
    public AprsWeatherBuilder Messaging(bool capable = true)
    {
        messaging = capable;
        return this;
    }

    /// <summary>Sends the position in compressed (base-91) form, with the wind in the course and
    /// speed bytes (APRS12c §9). Only with a position.</summary>
    public AprsWeatherBuilder Compressed(bool compressed = true)
    {
        this.compressed = compressed;
        return this;
    }

    /// <summary>Adds a <c>!DAO!</c> extension carrying extra position precision, by default the WGS84
    /// base-91 form. Only with a position.</summary>
    public AprsWeatherBuilder Dao(AprsDao? dao = null)
    {
        this.dao = dao ?? AprsDao.Wgs84Base91;
        return this;
    }

    /// <summary>Wind direction in degrees clockwise from north, sustained one-minute speed in mph,
    /// and optionally the peak gust in the last five minutes in mph.</summary>
    public AprsWeatherBuilder Wind(int directionDegrees, double speedMph, int? gust = null)
    {
        weather = weather with { WindDirectionDegrees = directionDegrees, WindSpeedMph = speedMph, WindGustMph = gust ?? weather.WindGustMph };
        return this;
    }

    /// <summary>The peak wind gust in the last five minutes, in mph.</summary>
    public AprsWeatherBuilder Gust(int mph)
    {
        weather = weather with { WindGustMph = mph };
        return this;
    }

    /// <summary>Temperature in degrees Fahrenheit.</summary>
    public AprsWeatherBuilder Temperature(int fahrenheit)
    {
        weather = weather with { TemperatureFahrenheit = fahrenheit };
        return this;
    }

    /// <summary>Temperature in degrees Celsius, sent to the nearest degree Fahrenheit.</summary>
    public AprsWeatherBuilder TemperatureCelsius(double celsius) =>
        Temperature((int)Math.Round((celsius * 9 / 5) + 32, MidpointRounding.AwayFromZero));

    /// <summary>Relative humidity, 1-100 percent.</summary>
    public AprsWeatherBuilder Humidity(int percent)
    {
        weather = weather with { HumidityPercent = percent };
        return this;
    }

    /// <summary>Barometric pressure in millibars (hPa), sent to a tenth.</summary>
    public AprsWeatherBuilder Pressure(double millibars)
    {
        weather = weather with { PressureMillibars = millibars };
        return this;
    }

    /// <summary>Rainfall in inches over the last hour, the last 24 hours and since local midnight; each is optional.</summary>
    public AprsWeatherBuilder Rain(double? lastHour = null, double? last24Hours = null, double? sinceMidnight = null)
    {
        weather = weather with
        {
            RainLastHourInches = lastHour ?? weather.RainLastHourInches,
            RainLast24HoursInches = last24Hours ?? weather.RainLast24HoursInches,
            RainSinceMidnightInches = sinceMidnight ?? weather.RainSinceMidnightInches,
        };
        return this;
    }

    /// <summary>Rainfall in millimetres, as for <see cref="Rain"/>, sent in inches.</summary>
    public AprsWeatherBuilder RainMillimetres(double? lastHour = null, double? last24Hours = null, double? sinceMidnight = null) =>
        Rain(lastHour / 25.4, last24Hours / 25.4, sinceMidnight / 25.4);

    /// <summary>Luminosity in watts per square metre, 0-1999.</summary>
    public AprsWeatherBuilder Luminosity(int wattsPerSquareMetre)
    {
        weather = weather with { LuminosityWattsPerSquareMetre = wattsPerSquareMetre };
        return this;
    }

    /// <summary>Snowfall in the last 24 hours, in inches.</summary>
    public AprsWeatherBuilder Snowfall(decimal inches)
    {
        weather = weather with { SnowfallLast24HoursInches = inches };
        return this;
    }

    /// <summary>Every observation at once, replacing any set before.</summary>
    public AprsWeatherBuilder Observations(AprsWeather weather)
    {
        ArgumentNullException.ThrowIfNull(weather);
        this.weather = weather;
        return this;
    }

    /// <summary>A position report with the weather station symbol when a position was given,
    /// otherwise a positionless weather report.</summary>
    public override AprsData ToData()
    {
        if (position is { } p)
        {
            return new AprsPositionReport
            {
                Position = p,
                Symbol = symbol,
                Weather = weather,
                MessagingCapable = messaging,
                Timestamp = timestamp ?? (time is { } t ? AprsTimestamp.FromDateTime(t) : null),
                IsCompressed = compressed,
                Dao = dao,
            };
        }

        if (messaging || compressed || dao is not null || symbol != AprsSymbol.WeatherStation)
        {
            throw new InvalidOperationException("a symbol, messaging, compression and DAO need a position: call At(latitude, longitude)");
        }

        return new AprsWeatherReport
        {
            Timestamp = timestamp ?? AprsTimestamp.FromDateTime(time ?? DateTime.UtcNow, AprsTimestampFormat.MonthDayHoursMinutesUtc),
            Weather = weather,
        };
    }
}

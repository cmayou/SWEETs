using System;
using System.Collections.Generic;
using System.Linq;

namespace VendorGrid.Settings
{
    /// <summary>A procurement country. It decides the currency and the regional endpoint used for each vendor.</summary>
    public sealed class Country
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Currency { get; set; }
        public string DigiKeySite { get; set; }     // X-DIGIKEY-Locale-Site
        public string NewarkStore { get; set; }     // element14 storeInfo.id
        public string RsDomain { get; set; }        // RS Components portal
        public string Display { get { return Name + " (" + Code + ")"; } }

        public string RsSearchUrl
        {
            get
            {
                return RsDomain == "us.rs-online.com"
                    ? "https://us.rs-online.com/catalogsearch/result/?q={pn}"
                    : "https://" + RsDomain + "/web/c/?searchTerm={pn}";
            }
        }
    }

    public static class Regions
    {
        // code | name | currency | Digi-Key locale site | Newark/element14 store | RS portal
        static readonly string[] Rows =
        {
            "US|United States|USD|US|www.newark.com|us.rs-online.com",
            "CA|Canada|CAD|CA|canada.newark.com|us.rs-online.com",
            "MX|Mexico|USD|MX|mexico.newark.com|us.rs-online.com",
            "GB|United Kingdom|GBP|UK|uk.farnell.com|uk.rs-online.com",
            "IE|Ireland|EUR|IE|ie.farnell.com|ie.rs-online.com",
            "DE|Germany|EUR|DE|de.farnell.com|de.rs-online.com",
            "AT|Austria|EUR|AT|at.farnell.com|at.rs-online.com",
            "CH|Switzerland|CHF|CH|ch.farnell.com|ch.rs-online.com",
            "FR|France|EUR|FR|fr.farnell.com|fr.rs-online.com",
            "BE|Belgium|EUR|BE|be.farnell.com|be.rs-online.com",
            "NL|Netherlands|EUR|NL|nl.farnell.com|nl.rs-online.com",
            "IT|Italy|EUR|IT|it.farnell.com|it.rs-online.com",
            "ES|Spain|EUR|ES|es.farnell.com|es.rs-online.com",
            "PT|Portugal|EUR|PT|pt.farnell.com|pt.rs-online.com",
            "SE|Sweden|SEK|SE|se.farnell.com|se.rs-online.com",
            "NO|Norway|NOK|NO|no.farnell.com|no.rs-online.com",
            "DK|Denmark|DKK|DK|dk.farnell.com|dk.rs-online.com",
            "FI|Finland|EUR|FI|fi.farnell.com|fi.rs-online.com",
            "PL|Poland|PLN|PL|pl.farnell.com|pl.rs-online.com",
            "CZ|Czech Republic|CZK|CZ|cz.farnell.com|cz.rs-online.com",
            "HU|Hungary|HUF|HU|hu.farnell.com|hu.rs-online.com",
            "RO|Romania|RON|RO|ro.farnell.com|ro.rs-online.com",
            "GR|Greece|EUR|GR|export.farnell.com|gr.rs-online.com",
            "JP|Japan|JPY|JP|jp.element14.com|jp.rs-online.com",
            "CN|China|CNY|CN|cn.element14.com|cn.rs-online.com",
            "HK|Hong Kong|HKD|HK|hk.element14.com|hk.rs-online.com",
            "TW|Taiwan|TWD|TW|tw.element14.com|tw.rs-online.com",
            "KR|South Korea|KRW|KR|kr.element14.com|kr.rs-online.com",
            "IN|India|INR|IN|in.element14.com|in.rs-online.com",
            "SG|Singapore|SGD|SG|sg.element14.com|sg.rs-online.com",
            "MY|Malaysia|MYR|MY|my.element14.com|my.rs-online.com",
            "TH|Thailand|THB|TH|th.element14.com|th.rs-online.com",
            "AU|Australia|AUD|AU|au.element14.com|au.rs-online.com",
            "NZ|New Zealand|NZD|NZ|nz.element14.com|nz.rs-online.com",
            "BR|Brazil|USD|BR|export.farnell.com|us.rs-online.com",
            "AR|Argentina|USD|AR|export.farnell.com|us.rs-online.com",
            "CL|Chile|USD|CL|export.farnell.com|us.rs-online.com",
            "IL|Israel|USD|IL|il.farnell.com|il.rs-online.com",
            "ZA|South Africa|USD|ZA|export.farnell.com|za.rs-online.com",
            "AE|United Arab Emirates|USD|US|export.farnell.com|ae.rs-online.com",
            "XX|Other / International|USD|US|export.farnell.com|www.rs-online.com"
        };

        public static readonly IList<Country> All = Rows.Select(Parse).ToList();

        static Country Parse(string row)
        {
            var p = row.Split(new[] { '|' });
            return new Country { Code = p[0], Name = p[1], Currency = p[2], DigiKeySite = p[3], NewarkStore = p[4], RsDomain = p[5] };
        }

        public static Country Find(string code)
        {
            return All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)) ?? All[0];
        }
    }
}

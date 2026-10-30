using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class CrmHistoryTests
{
    // Real attribute routing, form model binding/validation, antiforgery where
    // declared, canonical rate limiting, controller persistence and capture.
    // Tracking ownership is a trusted synthetic middleware fixture; outbound
    // providers are the same explicit fakes as the controller-boundary matrix.
    [Theory]
    [InlineData("LifeQuoteController", "SubmitLifeQuote", "life_general", "quote_life")]
    [InlineData("LifeQuoteController", "SubmitTermLifeQuote", "life_term", "quote_term_life")]
    [InlineData("LifeQuoteController", "SubmitWholeLifeQuote", "life_whole", "quote_whole_life")]
    [InlineData("LifeQuoteController", "SubmitFinalExpenseQuote", "life_finalexpense", "quote_final_expense")]
    [InlineData("LifeQuoteController", "SubmitMortgageQuote", "life_mp", "quote_mortgage_protection")]
    [InlineData("LifeQuoteController", "SubmitIulQuote", "life_iul", "quote_iul")]
    [InlineData("AutoQuoteController", "Auto", "auto", "quote_auto")]
    [InlineData("HomeQuoteController", "SubmitHomeQuote", "home", "quote_home")]
    [InlineData("CommercialQuoteController", "Commercial", "commercial", "quote_commercial")]
    [InlineData("DisabilityQuoteController", "SubmitDisabilityQuote", "disability", "quote_disability")]
    [InlineData("DentalVisionHearingQuoteController", "SubmitDentalVisionHearingQuote", "dental_vision_hearing", "quote_dvh")]
    [InlineData("RiskAssessmentController", "SubmitRiskAssessment", "risk_assessment", "risk_assessment")]
    public Task InsuranceHttpRoutesValidateBeforeFilingAndPreserveExactRetries(string controllerName,string actionName,string product,string pageKey)
        => VerifyInsuranceSubmission(controllerName,actionName,product,pageKey,true);

    private static string CompleteInsuranceFixture(string original,string controllerName)
    {
        var values=JsonNode.Parse(original)!.AsObject();
        // Explicit synthetic answers, not reflection-generated validation defaults.
        // Fields irrelevant to a particular model are ignored by deserialization.
        var complete=JsonNode.Parse("""
        {"Gender":"Not Specified","DOB":"1990-01-01","MaritalStatus":"Single","DLStatus":"Not Licensed",
         "Industry":"Other","PrimaryAddress":"1 Synthetic Street","PrimaryCity":"Phoenix","PrimaryState":"AZ",
         "PrimaryCountry":"US","PrimaryPostalCode":"85001","PrimaryYearsAtAddress":"5","PhoneType":"Mobile",
         "EmailType":"Primary","PreferredContactMethod":"Email","BestTimeToContact":"Morning",
         "PriorCarrier":"Synthetic Carrier","PriorPolicyExpirationDate":"2027-01-01","CurrentPolicyExpirationDate":"2027-01-01",
         "PriorLiabilityLimits":"100/300","PriorPolicyTerm":"12","YearsWithPriorCarrier":"2","MonthsWithPriorCarrier":"0",
         "YearsContinuousCoverage":"2","MonthsContinuousCoverage":"0","CreditCheckAuthorized":"No","NewPolicyTerm":"12",
         "PackagePolicy":"No","NewPolicyEffectiveDate":"2027-01-01","Paperless":"No","MultiPolicyDiscount":"No",
         "BodilyInjury":"100/300","UninsuredMotorist":"100/300","UnderinsuredMotorist":"100/300","MedicalPayments":"1000","ResidenceType":"Owner",
         "PolicyFormType":"Homeowners","QuoteAsPackage":"No","CancelledDeclinedNonRenewedLast5Years":"No",
         "HomeUnderConstruction":"No","BusinessOrDaycareOnPremises":"No","SwimmingPoolOnPremises":"No","DogsOnPremises":"No",
         "NumberOfAnimalsOnPremises":"0","LapseInCoveragePast12Months":"No","AutoYearsWithPriorCarrierOrAgent":"2",
         "DwellingUsage":"Primary","OccupancyType":"Owner","DwellingType":"Single Family","NumberOfOccupants":"1","NumberOfStories":"1",
         "SquareFootage":"1000","YearBuilt":"2000","ConstructionStyle":"Ranch","RoofTypeMainMaterial":"Shingle","FoundationType":"Slab",
         "RoofDesign":"Gable","ExteriorWalls":"Brick","FullBaths":"1","PurchaseDate":"2020-01-01","DistanceFromFireStationMiles":"1",
         "FeetFromHydrant":"100","HeatingUpdate":"No","ElectricalUpdate":"No","PlumbingUpdate":"No","RoofingUpdate":"No",
         "DwellingCoverage":"250000","EstReplacementCost":"250000","PersonalLiability":"100000","AllPerilsDeductible":"1000",
         "BusinessName":"Synthetic Business","BusinessDescription":"Synthetic test only","GrossSales":100000,"TotalPayroll":0,
         "NumberOfEmployees":"1","StreetAddress":"1 Synthetic Street","City":"Phoenix","ZipCode":"85001","EffectiveDate":"2027-01-01",
         "InterestedIn":["General Liability","Property"],"EmploymentType":"Employed","IncomeProtectionImportance":"Important",
         "IncomeRange":"50000-75000","CoverageType":"Dental Vision Hearing","CurrentCoverage":"None","Timeline":"Soon"}
        """)!.AsObject();
        foreach(var item in complete)values[item.Key]=item.Value?.DeepClone();
        if(controllerName=="CommercialQuoteController")values["YearBuilt"]=2000;
        if(controllerName=="AutoQuoteController")
        {
            foreach(var item in values["Drivers"]!.AsArray())
            {
                var driver=item!.AsObject();
                foreach(var pair in new Dictionary<string,string> { ["DOB"]="1990-01-01",["Gender"]="Not Specified",["MaritalStatus"]="Single",
                    ["OccupationIndustry"]="Other",["OccupationTitle"]="Synthetic",["DLStatus"]="Not Licensed",["LicenseSuspendedLast5Years"]="No",
                    ["TelematicsDiscount"]="No",["MilitaryService"]="No" })driver[pair.Key]=pair.Value;
            }
            var index=0;
            foreach(var item in values["Vehicles"]!.AsArray())
            {
                var vehicle=item!.AsObject();
                foreach(var pair in new Dictionary<string,string> { ["VIN"]="1HGCM82633A004352",["Year"]="2020",["PurchaseDate"]="2020-01-01",
                    ["Use"]="Pleasure",["AnnualMiles"]="1000",["Performance"]="Standard",["OwnershipType"]="Owned",["Comprehensive"]="500",
                    ["Collision"]="500",["Towing"]="No",["Rental"]="No",["SpecialEquipment"]="No",["BrandedTitle"]="No" })vehicle[pair.Key]=pair.Value;
                vehicle["AssignedDriverIndex"]=index++;
            }
        }
        return values.ToJsonString();
    }

    private sealed class InsuranceHttpFixture : IDisposable
    {
        private readonly TestServer server;
        private readonly HttpClient client;
        private readonly string route;
        private readonly Dictionary<string,string> answers;
        private readonly string token;
        private InsuranceHttpFixture(TestServer server,HttpClient client,string route,Dictionary<string,string> answers,string token)
        { this.server=server;this.client=client;this.route=route;this.answers=answers;this.token=token; }

        internal static async Task<InsuranceHttpFixture> Create(Type type,MethodInfo action,Type modelType,
            Dictionary<Type,object> known,Func<Type,object> dependency,IDataProtectionProvider protector,
            AgentTrackingProfile profile,string answerJson,DefaultHttpContext original)
        {
            var server=new TestServer(new WebHostBuilder().UseEnvironment("Testing").ConfigureServices(services=>
            {
                foreach(var item in known)services.AddSingleton(item.Key,item.Value);
                foreach(var parameter in type.GetConstructors().Single().GetParameters())
                    if(!known.ContainsKey(parameter.ParameterType))services.AddSingleton(parameter.ParameterType,dependency(parameter.ParameterType));
                services.AddSingleton(protector);services.AddAntiforgery();
                services.AddRateLimiter(PlatformRateLimiting.ConfigurePolicies);
                services.AddControllersWithViews(options=>options.Filters.Add<InsuranceInvalidViewResult>()).AddApplicationPart(type.Assembly);
            }).Configure(app=>
            {
                app.Use(async(context,next)=>
                {
                    context.Items["TrackingProfile"]=profile;context.Items["TrackingSlug"]=profile.Slug;context.Items["IsFounderPath"]=false;
                    if(context.Request.Path=="/__synthetic/antiforgery")
                    { await context.Response.WriteAsJsonAsync(context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken);return; }
                    await next();
                });
                app.UseRouting();app.UseRateLimiter();
                app.UseEndpoints(endpoints=>endpoints.MapControllerRoute("default","{controller=Home}/{action=Index}/{id?}"));
            }));
            var client=server.CreateClient();client.BaseAddress=new Uri("http://synthetic.example.test");
            client.DefaultRequestHeaders.Add("X-Requested-With","fetch");
            try
            {
                var bootstrap=await client.GetAsync("/__synthetic/antiforgery");bootstrap.EnsureSuccessStatusCode();
                client.DefaultRequestHeaders.Add("Cookie",string.Join("; ",bootstrap.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0])));
                var token=JsonSerializer.Deserialize<string>(await bootstrap.Content.ReadAsStringAsync())!;
                var route="/"+type.GetCustomAttribute<RouteAttribute>()!.Template.Trim('/')+"/"+action.GetCustomAttribute<HttpPostAttribute>()!.Template;
                var answers=new Dictionary<string,string>();
                using(var document=JsonDocument.Parse(JsonSerializer.Serialize(JsonSerializer.Deserialize(answerJson,modelType),modelType)))
                    Flatten(document.RootElement,"",answers);
                var fixture=new InsuranceHttpFixture(server,client,route,answers,token);
                var invalid=fixture.Fields(original.Request.Form);
                invalid[type.Name=="CommercialQuoteController"?"InsuredFirstName":"FirstName"]="";
                using(var response=await client.PostAsync(route,new FormUrlEncodedContent(invalid)))
                    Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
                var db=(MasterAppDbContext)known[typeof(MasterAppDbContext)];
                Assert.Equal(0,await db.WebsiteLeads.CountAsync());Assert.Equal(0,await db.WebsiteLeadIntakeLinks.CountAsync());Assert.Equal(0,await db.Set<CrmHistoryEntry>().CountAsync());
                if(action.IsDefined(typeof(ValidateAntiForgeryTokenAttribute)) || type.IsDefined(typeof(ValidateAntiForgeryTokenAttribute)))
                {
                    var missing=fixture.Fields(original.Request.Form);missing.Remove("__RequestVerificationToken");
                    using var response=await client.PostAsync(route,new FormUrlEncodedContent(missing));
                    Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
                    Assert.Equal(0,await db.WebsiteLeads.CountAsync());Assert.Equal(0,await db.WebsiteLeadIntakeLinks.CountAsync());Assert.Equal(0,await db.Set<CrmHistoryEntry>().CountAsync());
                }
                return fixture;
            }
            catch { client.Dispose();server.Dispose();throw; }
        }

        private Dictionary<string,string> Fields(IFormCollection source)
        {
            var fields=new Dictionary<string,string>(answers);
            foreach(var item in source)fields[item.Key]=item.Value.ToString();
            fields["__RequestVerificationToken"]=token;return fields;
        }
        internal async Task<IActionResult> Submit(IFormCollection source)
        {
            using var response=await client.PostAsync(route,new FormUrlEncodedContent(Fields(source)));
            var body=await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect,route+": "+response.StatusCode+" "+body);
            return new OkObjectResult(body);
        }
        private static void Flatten(JsonElement value,string prefix,Dictionary<string,string> fields)
        {
            if(value.ValueKind==JsonValueKind.Object)
                foreach(var item in value.EnumerateObject())Flatten(item.Value,prefix.Length==0?item.Name:prefix+"."+item.Name,fields);
            else if(value.ValueKind==JsonValueKind.Array)
            { var i=0;foreach(var item in value.EnumerateArray())Flatten(item,prefix+"["+(i++)+"]",fields); }
            else if(value.ValueKind!=JsonValueKind.Null)fields[prefix]=value.ValueKind==JsonValueKind.String?value.GetString()!:value.GetRawText();
        }
        public void Dispose(){client.Dispose();server.Dispose();}
    }

    // Isolate error-page rendering only. Never change model validity or turn a
    // failed submission into success; preserve the actual controller's errors.
    public sealed class InsuranceInvalidViewResult : IAsyncResultFilter
    {
        public async Task OnResultExecutionAsync(ResultExecutingContext context,ResultExecutionDelegate next)
        {
            if(context.Result is ViewResult)
                context.Result=new BadRequestObjectResult(context.ModelState.Where(x=>x.Value!.Errors.Count>0)
                    .ToDictionary(x=>x.Key,x=>x.Value!.Errors.Select(error=>error.ErrorMessage).ToArray()));
            await next();
        }
    }
}

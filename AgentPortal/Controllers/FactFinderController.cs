using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Models;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AgentPortal.Controllers
{
    [AssistantBlock]
public class FactFinderController : Controller
    {
        // =========================================================
        // THEME (match your gold)
        // =========================================================
        private static readonly string Gold = "#a68023";
        private static readonly string GoldDark = "#8f6f1e";
        private static readonly string Ink = "#111111";
        private static readonly string Muted = "#000000";

        // =========================================================
        // ROUTES (so /FactFinder/Senior etc do NOT 404)
        // =========================================================
        [HttpGet]
        public IActionResult Index()
        {
            var vm = new FactFinderViewModel();
            EnsureDefaults(vm);
            return View(vm); // Views/FactFinder/Index.cshtml
        }

        [HttpGet]
        public IActionResult Senior()
        {
            var vm = new FactFinderViewModel { FormType = "Senior" };
            EnsureDefaults(vm);
            return View("Senior", vm);
        }

        [HttpGet]
        public IActionResult MiddleAged()
        {
            var vm = new FactFinderViewModel { FormType = "Middle" };
            EnsureDefaults(vm);
            return View("MiddleAged", vm);
        }

        [HttpGet]
        public IActionResult Younger()
        {
            var vm = new FactFinderViewModel { FormType = "Young" };
            EnsureDefaults(vm);
            return View("Younger", vm);
        }

        // =========================================================
        // SUBMIT -> DOWNLOAD PDF
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Submit(FactFinderViewModel model)
        {
            EnsureDefaults(model);
            NormalizeModelState(model);

            if (!ModelState.IsValid)
            {
                return model.FormType switch
                {
                    "Senior" => View("Senior", model),
                    "Middle" => View("MiddleAged", model),
                    "Young"  => View("Younger", model),
                    _        => View("Index", model)
                };
            }

            QuestPDF.Settings.License = LicenseType.Community;

            var pdfBytes = BuildPdfBytes(model);

            var client = GetClientName(model);
            var fileName = $"{Sanitize(client)}_FactFinder_{DateTime.Now:yyyy}.pdf";

            return File(pdfBytes, "application/pdf", fileName);
        }

        // =========================================================
        // PDF: FULL RENDER (ALL QUESTIONS / ALL FIELDS)
        // =========================================================
        private static byte[] BuildPdfBytes(FactFinderViewModel model)
        {
            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(26);
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor(Ink));

                    page.Header().Element(h => PdfHeader(h, model));

                    page.Content().PaddingTop(10).Element(c =>
                    {
                        c.Column(col =>
                        {
                            col.Spacing(12);

                            var ft = (model.FormType ?? "").Trim();

                            if (ft.Equals("Senior", StringComparison.OrdinalIgnoreCase))
                                RenderSenior(col, model.Senior);
                            else if (ft.Equals("Middle", StringComparison.OrdinalIgnoreCase))
                                RenderMiddle(col, model.Middle);
                            else if (ft.Equals("Young", StringComparison.OrdinalIgnoreCase))
                                RenderYoung(col, model.Young);
                            else
                                col.Item().Text("Unknown FormType — cannot render.").FontSize(12).SemiBold();

                            col.Item().PaddingTop(6).Element(DisclaimerCard);
                        });
                    });

                    page.Footer()
                        .AlignCenter()
                        .Text("Protect your health. Protect your wealth. Protect your family. Protect your legacy.")
                        .FontSize(9)
                        .FontColor(Muted);
                });
            });

            return doc.GeneratePdf();
        }

        // =========================================================
        // PDF HELPERS
        // =========================================================
        private static void PdfHeader(IContainer container, FactFinderViewModel model)
        {
            container.PaddingBottom(8).Column(col =>
            {
                col.Item().Row(r =>
                {
                    r.RelativeItem().Column(c =>
                    {
                        c.Item().Text("LEGEND™ LEGACY PROTECTION").FontSize(16).SemiBold().FontColor(GoldDark);
                        c.Item().Text($"Fact Finder — {LabelFormType(model.FormType)}").FontSize(11).SemiBold();
                        c.Item().Text($"Generated: {DateTime.Now:MMMM dd, yyyy}").FontSize(9).FontColor(Muted);
                    });

                    r.ConstantItem(240).AlignRight().AlignMiddle().Column(c =>
                    {
                        var client = GetClientName(model);
                        c.Item().Text($"Client: {NV(client)}").FontSize(10).SemiBold();
                    });
                });

                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Gold);
            });
        }

        private static string LabelFormType(string? ft) =>
            (ft ?? "").Trim() switch
            {
                "Senior" => "Senior",
                "Middle" => "Middle-Aged",
                "Young"  => "Younger",
                _        => "Unknown"
            };

        private static void Card(IContainer container, string title, Action<IContainer> body)
        {
            container
                .Border(2).BorderColor(Gold)
                .Background(Colors.White)
                .Padding(14)
                .CornerRadius(8)
                .Column(col =>
                {
                    col.Item().Text(title).FontSize(12).SemiBold().FontColor(Ink);
                    col.Item().PaddingTop(6).Element(body);
                });
        }

        private static void KV(IContainer container, string k, string v)
        {
            container.Row(r =>
            {
                r.ConstantItem(260).Text(k).FontColor(Muted);
                r.RelativeItem().Text(v);
            });
        }

        private static void Divider(IContainer c) =>
            c.PaddingVertical(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

        private static string NV(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();
        private static string Bool(bool v) => v ? "Yes" : "No";
private static string Bool(bool? v) => v.HasValue ? (v.Value ? "Yes" : "No") : "-";
        private static string D(DateTime? d) => d.HasValue ? d.Value.ToString("MM/dd/yyyy") : "-";

        private static string Money(decimal? v)
        {
            if (!v.HasValue) return "-";
            return v.Value.ToString("C0", CultureInfo.GetCultureInfo("en-US"));
        }

        private static void DisclaimerCard(IContainer container)
        {
            Card(container, "Disclaimer", c =>
            {
                c.Column(col =>
                {
                    col.Spacing(4);
                    col.Item().Text("Educational fact-finding only. No investment advice is provided.")
                        .FontSize(9).FontColor(Muted);
                });
            });
        }

// =========================================================
// SENIOR: FULL — 100% aligned to Senior.cshtml + SeniorFactFinder model
// Mirrors the 4-step wizard order:
//   Part 1 = Priorities + Pain Points (Step 1)
//   Part 2 = Facts + Inventory (Step 2)
//   Part 3 = Life Insurance (Step 3)
//   Part 4 = Retirement + Assets (Step 4)
// =========================================================
private static void RenderSenior(ColumnDescriptor col, SeniorFactFinder s)
{
    s ??= new SeniorFactFinder();

    // -------------------------
    // PART 1 — PRIORITIES + PAIN POINTS (STEP 1)
    // -------------------------
    col.Item().Element(c => Card(c, "Part 1 — Priorities & Pain Points", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            var a  = s.Applicant ?? new SeniorApplicant();
            var m  = s.Medical ?? new SeniorMedical();
            var ec = s.ExtendedCare ?? new SeniorExtendedCare();

            // Quick Identity (Minimal)
            x.Item().Text("Quick Identity (Minimal)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Applicant Name*", NV(a.ApplicantName)));
            x.Item().Element(k => KV(k, "Date of Birth", D(a.DateOfBirth)));
            x.Item().Element(k => KV(k, "Phone", NV(a.Phone)));
            x.Item().Element(k => KV(k, "Email", NV(a.Email)));

            x.Item().PaddingTop(6).Element(Divider);

            // Medical Expenses — Priorities
            x.Item().Text("Medical Expenses — Priorities").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "1) Protect yourself plan", NV(m.ProtectYourselfPlan)));
            x.Item().Element(k => KV(k, "2) Health in last three years", NV(m.HealthLastThreeYears)));
            x.Item().Element(k => KV(k, "3) Current medications", NV(m.CurrentMedications)));
            x.Item().Element(k => KV(k, "4) Family history (Cancer/Stroke/Heart)", Bool(m.FamilyHistoryCancerStrokeHeart)));
            x.Item().Element(k => KV(k, "Impact on family/finances", NV(m.FamilyHistoryImpact)));
            x.Item().Element(k => KV(k, "5) Change anything about present coverage", NV(m.ChangeAboutPresentCoverage)));
            x.Item().Element(k => KV(k, "6) Strategy outside plan coverage", NV(m.StrategyCoverOutsidePlansCoverage)));
            x.Item().Element(k => KV(k, "Learn ways to avoid out-of-pocket costs?", Bool(m.WouldLikeLearnAvoidOutOfPocket)));

            x.Item().PaddingTop(6).Element(Divider);

            // Extended Care — Priorities
            x.Item().Text("Extended Care — Priorities").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Do you have extended care coverage?", Bool(ec.HasExtendedCareCoverage)));
            x.Item().Element(k => KV(k, "8a) Looked into it?", Bool(ec.LookedIntoIt)));
            x.Item().Element(k => KV(k, "8b) Why/why not important?", NV(ec.WhyNotImportant)));
            x.Item().Element(k => KV(k, "8c) What prevented moving forward?", NV(ec.WhatPreventedMovingForward)));
            x.Item().Element(k => KV(k, "9) Who needed extended/recovery care?", NV(ec.KnowSomeoneNeededCare)));
            x.Item().Element(k => KV(k, "10) Financial impact story", NV(ec.FinanciallyImpactedStory)));
            x.Item().Element(k => KV(k, "11) Biggest concern choice", NV(ec.BiggestConcernChoice)));
            x.Item().Element(k => KV(k, "Why?", NV(ec.BiggestConcernWhy)));
            x.Item().Element(k => KV(k, "12) Children involvement view", NV(ec.ChildrenInvolvementView)));
            x.Item().Element(k => KV(k, "13) Family conversations (aging in place)", NV(ec.FamilyConversationsAgingInPlace)));
        });
    }));

    // -------------------------
    // PART 2 — FACTS + INVENTORY (STEP 2)
    // -------------------------
    col.Item().Element(c => Card(c, "Part 2 — Facts & Inventory", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            var a  = s.Applicant ?? new SeniorApplicant();
            var sp = s.Spouse ?? new SeniorSpouse();
            var m  = s.Medical ?? new SeniorMedical();
            var ec = s.ExtendedCare ?? new SeniorExtendedCare();

            // Applicant (Details)
            x.Item().Text("Applicant (Details)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Occupation", NV(a.Occupation)));
            x.Item().Element(k => KV(k, "Retired?", Bool(a.IsRetired)));
            x.Item().Element(k => KV(k, "Retired Year", a.RetiredYear?.ToString() ?? "-"));

            x.Item().PaddingTop(4).Text("Benefits — Applicant").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Pension", Bool(a.Benefit_Pension)));
            x.Item().Element(k => KV(k, "Health Plan", Bool(a.Benefit_HealthPlan)));
            x.Item().Element(k => KV(k, "Other", Bool(a.Benefit_Other)));
            x.Item().Element(k => KV(k, "Other (Explain)", NV(a.BenefitOtherText)));

            x.Item().PaddingTop(4).Text("Address").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Address", NV(a.Address)));
            x.Item().Element(k => KV(k, "City", NV(a.City)));
            x.Item().Element(k => KV(k, "State", NV(a.State)));
            x.Item().Element(k => KV(k, "ZIP", NV(a.Zip)));

            x.Item().PaddingTop(6).Element(Divider);

            // Spouse / Household
            x.Item().Text("Spouse / Household").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Spouse Name", NV(sp.SpouseName)));
            x.Item().Element(k => KV(k, "Spouse DOB", D(sp.DateOfBirth)));
            x.Item().Element(k => KV(k, "Spouse Occupation", NV(sp.Occupation)));
            x.Item().Element(k => KV(k, "Spouse Retired?", Bool(sp.IsRetired)));
            x.Item().Element(k => KV(k, "Spouse Retired Year", sp.RetiredYear?.ToString() ?? "-"));

            x.Item().PaddingTop(4).Text("Benefits — Spouse").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Pension", Bool(sp.Benefit_Pension)));
            x.Item().Element(k => KV(k, "Health Plan", Bool(sp.Benefit_HealthPlan)));
            x.Item().Element(k => KV(k, "Other", Bool(sp.Benefit_Other)));
            x.Item().Element(k => KV(k, "Other (Explain)", NV(sp.BenefitOtherText)));

            x.Item().Element(k => KV(k, "Spouse Email", NV(sp.Email)));
            x.Item().Element(k => KV(k, "Spouse Phone", NV(sp.Phone)));

            x.Item().PaddingTop(6).Element(Divider);

            // Children / Dependents
            x.Item().Text("Children / Dependents").SemiBold().FontColor(GoldDark);
            var kids = s.Children ?? new List<SeniorChild>();
            x.Item().Table(t =>
            {
                t.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn(3);
                    cols.RelativeColumn(1);
                    cols.RelativeColumn(2);
                });

                t.Header(h =>
                {
                    headerCell(h.Cell(), "Name");
                    headerCell(h.Cell(), "Age");
                    headerCell(h.Cell(), "City");
                });

                foreach (var ch in kids)
                {
                    cell(t.Cell(), NV(ch?.ChildName));
                    cell(t.Cell(), ch?.Age?.ToString() ?? "-");
                    cell(t.Cell(), NV(ch?.City));
                }

                static void headerCell(IContainer c, string text) =>
                    c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                static void cell(IContainer c, string text) =>
                    c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
            });

            x.Item().Element(k => KV(k, "Ages of Grandchildren", NV(s.AgesOfGrandchildren)));

            x.Item().PaddingTop(6).Element(Divider);

            // Emergency Contact
            var em = s.EmergencyContact ?? new SeniorEmergencyContact();
            x.Item().Text("Emergency Contact").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Name", NV(em.Name)));
            x.Item().Element(k => KV(k, "Phone", NV(em.Phone)));
            x.Item().Element(k => KV(k, "Email", NV(em.Email)));

            x.Item().PaddingTop(6).Element(Divider);

            // Appointment (Agent Use)
            x.Item().Text("Appointment (Agent Use)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Source of Visit", NV(s.SourceOfVisit)));
            x.Item().Element(k => KV(k, "Agent(s)", NV(s.Agents)));
            x.Item().Element(k => KV(k, "Date of Appointment", D(s.DateOfAppointment)));

            x.Item().PaddingTop(6).Element(Divider);

            // Medical Coverage Snapshot (Inventory)
            x.Item().Text("Medical Coverage Snapshot (Inventory)").SemiBold().FontColor(GoldDark);

            x.Item().PaddingTop(4).Text("Coverage Review — Applicant").SemiBold().FontColor(GoldDark);
            coverageBlock(x, m.ApplicantCoverage ?? new SeniorMedicalCoverage());

            x.Item().PaddingTop(6).Text("Coverage Review — Spouse").SemiBold().FontColor(GoldDark);
            coverageBlock(x, m.SpouseCoverage ?? new SeniorMedicalCoverage());

            x.Item().PaddingTop(6).Element(Divider);

            // Parents
            x.Item().PaddingTop(4).Text("Applicant Parents (Age / Cause of Death-Age at Death)").SemiBold().FontColor(GoldDark);
            parentTable(x, m.ApplicantParents);

            x.Item().PaddingTop(4).Text("Spouse Parents (Age / Cause of Death-Age at Death)").SemiBold().FontColor(GoldDark);
            parentTable(x, m.SpouseParents);

            x.Item().PaddingTop(6).Element(Divider);

            // Extended Care — Current Policy Details (If Applicable)
            x.Item().Text("Extended Care — Current Policy Details (If Applicable)").SemiBold().FontColor(GoldDark);
            var pol = ec.CurrentPolicy ?? new SeniorExtendedCarePolicy();
            x.Item().Element(k => KV(k, "Benefits Covered", NV(pol.BenefitsCovered)));
            x.Item().Element(k => KV(k, "Benefit Period", NV(pol.BenefitPeriod)));
            x.Item().Element(k => KV(k, "Elimination Period", NV(pol.EliminationPeriod)));
            x.Item().Element(k => KV(k, "Premium", Money(pol.Premium)));
            x.Item().Element(k => KV(k, "Company", NV(pol.Company)));
            x.Item().Element(k => KV(k, "Benefit Amount", Money(pol.BenefitAmount)));
            x.Item().Element(k => KV(k, "Inflation Protection", NV(pol.InflationProtection)));

            // ---- local helpers (exact coverage fields from cshtml/model)
            static void coverageBlock(ColumnDescriptor x, SeniorMedicalCoverage c)
            {
                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                    });

                    row(t, "None", c.None ? "Yes" : "No");
                    row(t, "Original Medicare", c.OriginalMedicare ? "Yes" : "No");
                    row(t, "Medicaid", c.Medicaid ? "Yes" : "No");
                    row(t, "Group", c.Group ? "Yes" : "No");
                    row(t, "Med Supp", c.MedSupp ? "Yes" : "No");
                    row(t, "MA", c.MA ? "Yes" : "No");
                    row(t, "HIP/CI", c.HIP_CI ? "Yes" : "No");

                    row(t, "Other", NV(c.Other));
                    row(t, "Company Name", NV(c.CompanyName));
                    row(t, "Plan", NV(c.Plan));
                    row(t, "Premium", Money(c.Premium));
                    row(t, "Drug Coverage", c.DrugCoverage.HasValue ? (c.DrugCoverage.Value ? "Yes" : "No") : "-");
                    row(t, "Provider/PCP", NV(c.ProviderPCP));

                    row(t, "Dental", c.Add_Dental ? "Yes" : "No");
                    row(t, "Vision", c.Add_Vision ? "Yes" : "No");
                    row(t, "Critical Illness", c.Add_CriticalIllness ? "Yes" : "No");
                    row(t, "Other Benefit", c.Add_Other ? "Yes" : "No");
                    row(t, "Other Benefits Text", NV(c.AddOtherText));

                    static void row(TableDescriptor t, string k, string v)
                    {
                        t.Cell()
                            .Padding(5).BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
                            .Text(k).FontSize(9).FontColor(Muted);

                        t.Cell().ColumnSpan(3)
                            .Padding(5).BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
                            .Text(v).FontSize(9);
                    }
                });
            }

            static void parentTable(ColumnDescriptor x, List<SeniorParentInfo> parents)
            {
                parents ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Parent");
                        header(h.Cell(), "Age");
                        header(h.Cell(), "Cause / Age at Death");
                    });

                    foreach (var p in parents)
                    {
                        cell(t.Cell(), NV(p?.Label));
                        cell(t.Cell(), p?.Age?.ToString() ?? "-");
                        cell(t.Cell(), NV(p?.CauseOfDeathOrAgeAtDeath));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));

    // -------------------------
    // PART 3 — FINAL EXPENSES / LIFE INSURANCE (STEP 3)
    // -------------------------
    col.Item().Element(c => Card(c, "Part 3 — Final Expenses / Life Insurance", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            var li = s.LifeInsurance ?? new SeniorLifeInsurance();

            x.Item().Element(k => KV(k, "14) Primary purpose of current life insurance", NV(li.PrimaryPurposeOfCurrentLifeInsurance)));

            x.Item().PaddingTop(6).Text("Applicant Policies").SemiBold().FontColor(GoldDark);
            policyTable(x, li.ApplicantPolicies);

            x.Item().PaddingTop(6).Text("Spouse Policies").SemiBold().FontColor(GoldDark);
            policyTable(x, li.SpousePolicies);

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Element(k => KV(k, "15) Why did you choose the type?", NV(li.WhyChoseType)));
            x.Item().Element(k => KV(k, "How did you choose that amount of benefit?", NV(li.HowChoseBenefitAmount)));
            x.Item().Element(k => KV(k, "16) When was it last reviewed?", NV(li.WhenLastReviewed)));
            x.Item().Element(k => KV(k, "Do you have a will/trust?", Bool(li.HasWillOrTrust)));
            x.Item().Element(k => KV(k, "17) Aware how Social Security works when one spouse passes?", Bool(li.AwareHowSocialSecurityWorksWhenOneSpousePasses)));
            x.Item().Element(k => KV(k, "Planning to cover future reduction in SS benefits", NV(li.PlanningToCoverFutureSSReduction)));
            x.Item().Element(k => KV(k, "18) Planning to leave an IRA to family?", Bool(li.PlanningToLeaveIRAtoFamily)));

            static void policyTable(ColumnDescriptor x, List<SeniorLifePolicy> policies)
            {
                policies ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(1);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Label");
                        header(h.Cell(), "Face");
                        header(h.Cell(), "Company");
                        header(h.Cell(), "Premium");
                        header(h.Cell(), "Type / Beneficiary / Values");
                    });

                    foreach (var p in policies)
                    {
                        cell(t.Cell(), NV(p?.Label));
                        cell(t.Cell(), Money(p?.FaceAmount));
                        cell(t.Cell(), NV(p?.Company));
                        cell(t.Cell(), Money(p?.Premium));

                        var details =
                            $"Type: {NV(p?.Type)} | Beneficiary: {NV(p?.PrimaryBeneficiary)} | Cash: {Money(p?.CashValue)} | Surrender: {Money(p?.SurrenderValue)}";
                        cell(t.Cell(), details);
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));

    // -------------------------
    // PART 4 — RETIREMENT INCOME / SAVINGS / ASSETS (STEP 4)
    // -------------------------
    col.Item().Element(c => Card(c, "Part 4 — Retirement Income / Savings / Assets", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            var r = s.Retirement ?? new SeniorRetirement();

            // Retirement Priorities (Start Here) — Q21–30 + notes (matches cshtml)
            x.Item().Text("Retirement Priorities (Start Here)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "21) Concerns about outliving money", NV(r.OutlivingMoneyConcerns)));
            x.Item().Element(k => KV(k, "22) Still paying income tax?", Bool(r.StillPayingIncomeTax)));
            x.Item().Element(k => KV(k, "Priority: increase income / lower taxes / both", NV(r.PriorityIncreaseIncomeOrLowerTaxesOrBoth)));
            x.Item().Element(k => KV(k, "23) Monthly expenses notes", NV(r.MonthlyExpensesNotes)));
            x.Item().Element(k => KV(k, "24) Change in current financial plan", NV(r.WhatChangeInFinancialPlan)));
            x.Item().Element(k => KV(k, "25) Goals for this money", NV(r.GoalsForThisMoney)));
            x.Item().Element(k => KV(k, "26) Risk comfort level", NV(r.RiskComfortLevel)));
            x.Item().Element(k => KV(k, "27) Biggest concern (growth/income/safety)", NV(r.BiggestConcern_GrowthIncomeSafety)));
            x.Item().Element(k => KV(k, "28) Feelings about recent performance", NV(r.FeelAboutRecentPerformance)));
            x.Item().Element(k => KV(k, "Service received met expectations?", NV(r.FeelAboutServiceReceived)));
            x.Item().Element(k => KV(k, "29) Updated on SECURE Act legacy impact", NV(r.UpdatedOnSecureActImpact)));
            x.Item().Element(k => KV(k, "30) Story behind assets (property/inheritance)", NV(r.StoryBehindAssetsInheritance)));
            x.Item().Element(k => KV(k, "Outcomes / Additional Info / Follow-up Notes", NV(r.OutcomesAndFollowUpNotes)));

            x.Item().PaddingTop(6).Element(Divider);

            // Income (Monthly) — Numbers (Q19)
            x.Item().Text("19) Current sources of regular income (monthly) — Applicant").SemiBold().FontColor(GoldDark);
            incomeGroup(x, r.ApplicantIncome ?? new SeniorIncomeGroup());

            x.Item().PaddingTop(4).Text("19) Current sources of regular income (monthly) — Spouse").SemiBold().FontColor(GoldDark);
            incomeGroup(x, r.SpouseIncome ?? new SeniorIncomeGroup());

            x.Item().PaddingTop(6).Element(Divider);

            // Social Security / Pension Details (Q20)
            x.Item().Text("20) Social Security / Pension Details").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Receive Social Security?", Bool(r.ReceiveSocialSecurity)));
            x.Item().Element(k => KV(k, "SS monthly amount", Money(r.SocialSecurityMonthlyAmount)));
            x.Item().Element(k => KV(k, "Company pension monthly amount", Money(r.CompanyPensionMonthlyAmount)));
            x.Item().Element(k => KV(k, "Pension survivor benefits for spouse?", Bool(r.PensionHasSurvivorBenefitsForSpouse)));

            x.Item().PaddingTop(6).Element(Divider);

            // Assets — Numbers
            x.Item().Text("Assets — Numbers").SemiBold().FontColor(GoldDark);

            x.Item().PaddingTop(4).Text("Non-Liquid Assets").SemiBold().FontColor(GoldDark);
            var n = r.NonLiquidAssets ?? new SeniorNonLiquidAssets();
            x.Item().Element(k => KV(k, "Non-Qualified Annuities", Money(n.NonQualifiedAnnuities)));
            x.Item().Element(k => KV(k, "Life Insurance Cash Value", Money(n.LifeInsuranceCashValue)));
            x.Item().Element(k => KV(k, "Qualified IRAs & Annuities", Money(n.QualifiedIRAsAndAnnuities)));
            x.Item().Element(k => KV(k, "Other Investments (CDs)", Money(n.OtherInvestments_CDs)));
            x.Item().Element(k => KV(k, "Real Estate (excl. primary)", Money(n.RealEstateExcludingPrimaryResidence)));
            x.Item().Element(k => KV(k, "Primary Residence Value", Money(n.ValuePrimaryResidence)));

            x.Item().PaddingTop(6).Text("Liquid Assets").SemiBold().FontColor(GoldDark);
            var l = r.LiquidAssets ?? new SeniorLiquidAssets();
            x.Item().Element(k => KV(k, "Checking", Money(l.Checking)));
            x.Item().Element(k => KV(k, "Savings", Money(l.Savings)));
            x.Item().Element(k => KV(k, "Money Markets", Money(l.MoneyMarkets)));
            x.Item().Element(k => KV(k, "Mutual Funds", Money(l.MutualFunds)));
            x.Item().Element(k => KV(k, "Stocks, Bonds or Other", Money(l.StocksBondsOrOther)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Element(k => KV(k, "Aware how RMDs work?", Bool(r.AwareHowRMDsWork)));

            // Repeaters (as per model/cshtml wiring)
            x.Item().PaddingTop(6).Text("CDs").SemiBold().FontColor(GoldDark);
            cdsTable(x, r.CDs);

            x.Item().PaddingTop(6).Text("Annuities / IRAs").SemiBold().FontColor(GoldDark);
            annuityTable(x, r.AnnuitiesIRAs);

            x.Item().PaddingTop(6).Text("401(k)").SemiBold().FontColor(GoldDark);
            var k401 = r.K401 ?? new Senior401kGroup();
            x.Item().Element(k => KV(k, "Applicant Company", NV(k401.ApplicantCompany)));
            x.Item().Element(k => KV(k, "Applicant Value", Money(k401.ApplicantValue)));
            x.Item().Element(k => KV(k, "Spouse Company", NV(k401.SpouseCompany)));
            x.Item().Element(k => KV(k, "Spouse Value", Money(k401.SpouseValue)));

            x.Item().PaddingTop(6).Text("Other Assets").SemiBold().FontColor(GoldDark);
            otherAssetsTable(x, r.OtherAssets);

            x.Item().PaddingTop(6).Element(Divider);

            // Disclaimer acknowledgement (from cshtml)
            x.Item().Element(k => KV(k, "Acknowledged Disclaimer*", Bool(s.AcknowledgedDisclaimer)));

            // ---- local helpers (exact model fields)
            static void incomeGroup(ColumnDescriptor x, SeniorIncomeGroup g)
            {
                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                    });

                    row(t, "SS", Money(g.SS));
                    row(t, "Pension", Money(g.Pension));
                    row(t, "Employment", Money(g.Employment));
                    row(t, "Real Estate", Money(g.RealEstate));
                    row(t, "Investment", Money(g.Investment));
                    row(t, "RMD (Yr)", Money(g.RMD));
                    row(t, "Other", Money(g.Other));
                    row(t, "Total", Money(g.Total));

                    static void row(TableDescriptor t, string k, string v)
                    {
                        t.Cell().Padding(5).BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
                            .Text(k).FontSize(9).FontColor(Muted);

                        t.Cell().ColumnSpan(3).Padding(5).BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
                            .Text(v).FontSize(9);
                    }
                });
            }

            static void cdsTable(ColumnDescriptor x, List<SeniorCDHolding> cds)
            {
                cds ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Bank");
                        header(h.Cell(), "Value");
                        header(h.Cell(), "Rate");
                        header(h.Cell(), "Maturity");
                        header(h.Cell(), "Penalty");
                    });

                    foreach (var cd in cds)
                    {
                        cell(t.Cell(), NV(cd?.BankName));
                        cell(t.Cell(), Money(cd?.Value));
                        cell(t.Cell(), cd?.InterestRate?.ToString() ?? "-");
                        cell(t.Cell(), cd?.MaturityDate?.ToString("MM/dd/yyyy") ?? "-");
                        cell(t.Cell(), NV(cd?.Penalty));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }

            static void annuityTable(ColumnDescriptor x, List<SeniorAnnuityIraHolding> items)
            {
                items ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Company");
                        header(h.Cell(), "Type");
                        header(h.Cell(), "Value");
                        header(h.Cell(), "Rate");
                        header(h.Cell(), "Contract / Penalty Exp");
                    });

                    foreach (var a in items)
                    {
                        cell(t.Cell(), NV(a?.Company));
                        cell(t.Cell(), NV(a?.Type));
                        cell(t.Cell(), Money(a?.Value));
                        cell(t.Cell(), a?.InterestRate?.ToString() ?? "-");

                        var dates =
                            $"Contract: {(a?.ContractDate?.ToString("MM/dd/yyyy") ?? "-")} | Penalty Exp: {(a?.PenaltyExpirationDate?.ToString("MM/dd/yyyy") ?? "-")}";
                        cell(t.Cell(), dates);
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }

            static void otherAssetsTable(ColumnDescriptor x, List<SeniorOtherAsset> items)
            {
                items ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(4);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Type");
                        header(h.Cell(), "Value");
                        header(h.Cell(), "Additional Info");
                    });

                    foreach (var o in items)
                    {
                        cell(t.Cell(), NV(o?.Type));
                        cell(t.Cell(), Money(o?.Value));
                        cell(t.Cell(), NV(o?.AdditionalInformation));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));
}

// =========================================================
// MIDDLE: FULL (matches your model + MiddleAged.cshtml)
// =========================================================
private static void RenderMiddle(ColumnDescriptor col, MiddleFactFinder m)
{
    // Null-safe anchors (PDF must never crash on empty lists / null nested objects)
    m ??= new MiddleFactFinder();

    var app = m.Applicant ?? new MiddleApplicant();
    var sp = m.Spouse ?? new MiddleSpouse();
    var dir = m.Direction ?? new MiddleDirection();
    var ec = m.EmergencyContact ?? new MiddleEmergencyContact();
    var ben = m.ApplicantBenefits ?? new MiddleEmployerBenefits();

    var health = m.Health ?? new MiddleHealth();
    health.Additional ??= new MiddleHealthAdditional();

    var life = m.Life ?? new MiddleLife();
    life.Applicant ??= new();
    life.Spouse ??= new();

    var di = m.DI ?? new MiddleDI();
    var liab = m.Liability ?? new MiddleLiability();

    var cf = m.Cashflow ?? new MiddleCashflow();
    var debt = m.Debt ?? new MiddleDebt();
    debt.Other ??= new();

    var assets = m.Assets ?? new MiddleAssets();
    assets.Retirement ??= new();
    assets.Brokerage ??= new();

    m.RealEstate ??= new();

    var biz = m.Business ?? new MiddleBusiness();
    var eq = m.EquityComp ?? new MiddleEquityComp();
    var tax = m.Tax ?? new MiddleTax();
    var college = m.College ?? new MiddleCollege();
    var legacy = m.Legacy ?? new MiddleLegacy();

    // =========================================================
    // PART 1 — VALUES + IDENTITY + HOUSEHOLD CONTEXT (STEP 1)
    // =========================================================
    col.Item().Element(c => Card(c, "Part 1 — Values, Identity & Household", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Identity (Required)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Applicant Name", NV(app.Name)));
            x.Item().Element(k => KV(k, "Applicant DOB", D(app.DOB)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Contact (Optional)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Email", NV(app.Email)));
            x.Item().Element(k => KV(k, "Phone", NV(app.Phone)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Spouse / Partner (Optional)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Spouse Name", NV(sp.Name)));
            x.Item().Element(k => KV(k, "Spouse DOB", D(sp.DOB)));
            x.Item().Element(k => KV(k, "Spouse Occupation", NV(sp.Occupation)));
            x.Item().Element(k => KV(k, "Spouse Employer", NV(sp.Employer)));
            x.Item().Element(k => KV(k, "Spouse Annual Income", Money(sp.AnnualIncome)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("What Matters + What’s Heavy").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "34) Winning definition (10–15 yrs)", NV(dir.WinningDefinition)));
            x.Item().Element(k => KV(k, "35) Biggest fear if no change", NV(dir.BiggestFearIfNoChange)));
            x.Item().Element(k => KV(k, "36) Change one thing starting today", NV(dir.ChangeOneThing)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Children / Dependents").SemiBold().FontColor(GoldDark);
            dependentsTable(x, m.Dependents);

            x.Item().Element(k => KV(k, "Family Support Outside Household", NV(m.FamilySupportOutsideHousehold)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Emergency Contact").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Name", NV(ec.Name)));
            x.Item().Element(k => KV(k, "Phone", NV(ec.Phone)));
            x.Item().Element(k => KV(k, "Email", NV(ec.Email)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Appointment (Agent Use)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Source of Visit", NV(m.SourceOfVisit)));
            x.Item().Element(k => KV(k, "Agent(s)", NV(m.Agents)));
            x.Item().Element(k => KV(k, "Date of Appointment", D(m.DateOfAppointment)));

            static void dependentsTable(ColumnDescriptor x, System.Collections.Generic.List<MiddleDependent> deps)
            {
                deps ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Name");
                        header(h.Cell(), "Age");
                        header(h.Cell(), "Relationship");
                        header(h.Cell(), "Special Needs");
                    });

                    foreach (var d in deps)
                    {
                        cell(t.Cell(), NV(d.Name));
                        cell(t.Cell(), d.Age?.ToString() ?? "-");
                        cell(t.Cell(), NV(d.Relationship));
                        cell(t.Cell(), NV(d.SpecialNeeds));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));

    // =========================================================
    // PART 2 — HOUSEHOLD + CAREER + BENEFITS (STEP 2)
    // =========================================================
    col.Item().Element(c => Card(c, "Part 2 — Career, Income & Employer Benefits", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Applicant — Work & Stability").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Occupation / Role", NV(app.Occupation)));
            x.Item().Element(k => KV(k, "Employer / Company", NV(app.Employer)));
            x.Item().Element(k => KV(k, "Years in current role", app.YearsInRole?.ToString() ?? "-"));
            x.Item().Element(k => KV(k, "Income Type", NV(app.IncomeType)));
            x.Item().Element(k => KV(k, "Annual Income (Approx.)", Money(app.AnnualIncome)));
            x.Item().Element(k => KV(k, "Income Trend (last 3 years)", NV(app.IncomeTrend)));
            x.Item().Element(k => KV(k, "Biggest threats to income (next 5 yrs)", NV(app.IncomeThreats)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Employer Benefits Snapshot").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Health", Bool(ben.Health)));
            x.Item().Element(k => KV(k, "Dental", Bool(ben.Dental)));
            x.Item().Element(k => KV(k, "Vision", Bool(ben.Vision)));
            x.Item().Element(k => KV(k, "HSA", Bool(ben.HSA)));
            x.Item().Element(k => KV(k, "FSA", Bool(ben.FSA)));
            x.Item().Element(k => KV(k, "Group Life", Bool(ben.GroupLife)));
            x.Item().Element(k => KV(k, "Group Disability (DI)", Bool(ben.GroupDI)));
            x.Item().Element(k => KV(k, "401(k)/403(b)/Retirement Plan", Bool(ben.RetirementPlan)));
            x.Item().Element(k => KV(k, "Retirement Plan Match", NV(ben.RetMatch)));
            x.Item().Element(k => KV(k, "Vesting Schedule", NV(ben.Vesting)));
            x.Item().Element(k => KV(k, "Other Benefits", NV(ben.Other)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Quick Pressure Check").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "20) Biggest frustration with finances", NV(cf.BiggestFrustration)));
        });
    }));

    // =========================================================
    // PART 3 — HEALTH COVERAGE + RISKS (STEP 3)
    // =========================================================
    col.Item().Element(c => Card(c, "Part 3 — Health Coverage & Medical Exposure", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Health Coverage Snapshot").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "1) Current coverage summary", NV(health.CurrentCoverageSummary)));
            x.Item().Element(k => KV(k, "Carrier / Company", NV(health.Carrier)));
            x.Item().Element(k => KV(k, "Monthly Premium (Approx.)", Money(health.Premium)));
            x.Item().Element(k => KV(k, "Deductible (Individual/Family)", NV(health.Deductible)));
            x.Item().Element(k => KV(k, "Out-of-Pocket Max (Ind/Fam)", NV(health.OOPMax)));
            x.Item().Element(k => KV(k, "HSA? Balance?", NV(health.HSABalance)));
            x.Item().Element(k => KV(k, "2) Last review notes", NV(health.LastReviewNotes)));
            x.Item().Element(k => KV(k, "3) Medical bills surprises", NV(health.MedicalBillsSurprises)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Optional Coverage / Gap Fillers").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Critical Illness", Bool(health.Additional.CriticalIllness)));
            x.Item().Element(k => KV(k, "Accident", Bool(health.Additional.Accident)));
            x.Item().Element(k => KV(k, "Hospital Indemnity", Bool(health.Additional.HospitalIndemnity)));
            x.Item().Element(k => KV(k, "Short-Term Disability", Bool(health.Additional.ShortTermDI)));
            x.Item().Element(k => KV(k, "Long-Term Disability", Bool(health.Additional.LongTermDI)));
            x.Item().Element(k => KV(k, "Other", NV(health.Additional.Other)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Health Risk & History").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "4) Health last three years", NV(health.HealthLastThreeYears)));
            x.Item().Element(k => KV(k, "5) Current medications", NV(health.CurrentMeds)));
            x.Item().Element(k => KV(k, "6) Major diagnoses (last 10 yrs)", NV(health.MajorDiagnoses)));
            x.Item().Element(k => KV(k, "7) Surgeries/hospitalizations (last 10 yrs)", NV(health.SurgeriesHosp)));
            x.Item().Element(k => KV(k, "8) Family history", NV(health.FamilyHistory)));
            x.Item().Element(k => KV(k, "9) Impact if health event happens", NV(health.HealthEventImpact)));
        });
    }));

    // =========================================================
    // PART 4 — PROTECTION (LIFE, DI, LIABILITY) (STEP 4)
    // =========================================================
    col.Item().Element(c => Card(c, "Part 4 — Protection Planning", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Life Insurance Inventory").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "10) Primary purpose", NV(life.PrimaryPurpose)));
            x.Item().Element(k => KV(k, "11) Last reviewed", NV(life.LastReview)));
            x.Item().Element(k => KV(k, "12) What breaks first if you pass", NV(life.WhatBreaksFirst)));

            x.Item().PaddingTop(6).Text("Applicant Policies").SemiBold().FontColor(GoldDark);
            middleLifeTable(x, life.Applicant);

            x.Item().PaddingTop(6).Text("Spouse Policies").SemiBold().FontColor(GoldDark);
            middleLifeTable(x, life.Spouse);

            x.Item().PaddingTop(8).Element(Divider);

            x.Item().Text("Disability (DI)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "13) If cannot work impact", NV(di.IfCannotWorkImpact)));
            x.Item().Element(k => KV(k, "DI through work", NV(di.GroupDI)));
            x.Item().Element(k => KV(k, "Personal DI", NV(di.IndividualDI)));
            x.Item().Element(k => KV(k, "Benefit Amount / Month", NV(di.BenefitAmount)));
            x.Item().Element(k => KV(k, "Elimination Period", NV(di.Elimination)));
            x.Item().Element(k => KV(k, "Benefit Period", NV(di.BenefitPeriod)));
            x.Item().Element(k => KV(k, "14) Emergency fund coverage (months)", NV(di.EmergencyFundMonths)));

            x.Item().PaddingTop(8).Element(Divider);

            x.Item().Text("Liability / Big Risk Events").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "15) Umbrella coverage", NV(liab.Umbrella)));
            x.Item().Element(k => KV(k, "16) Major exposures", NV(liab.Exposures)));
            x.Item().Element(k => KV(k, "17) Claims / lawsuit / cancellation history", NV(liab.ClaimsHistory)));

            static void middleLifeTable(ColumnDescriptor x, System.Collections.Generic.List<MiddleLifePolicy> policies)
            {
                policies ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Type");
                        header(h.Cell(), "Company");
                        header(h.Cell(), "Face");
                        header(h.Cell(), "Premium");
                        header(h.Cell(), "Duration");
                        header(h.Cell(), "Details");
                    });

                    foreach (var p in policies)
                    {
                        cell(t.Cell(), NV(p.Type));
                        cell(t.Cell(), NV(p.Company));
                        cell(t.Cell(), Money(p.Face));
                        cell(t.Cell(), Money(p.Premium));
                        cell(t.Cell(), NV(p.Duration));
                        var details = $"Ben: {NV(p.Beneficiary)} | Cash: {Money(p.CashValue)} | Riders: {NV(p.Riders)}";
                        cell(t.Cell(), details);
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));

    // =========================================================
    // PART 5 — CASH FLOW + DEBT (STEP 5)
    // =========================================================
    col.Item().Element(c => Card(c, "Part 5 — Cash Flow & Debt", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Cash Flow & Monthly Structure").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Monthly Household Income (Net)", Money(cf.NetIncome)));
            x.Item().Element(k => KV(k, "Monthly Core Expenses", Money(cf.CoreExpenses)));
            x.Item().Element(k => KV(k, "Monthly Savings / Investing", Money(cf.Savings)));
            x.Item().Element(k => KV(k, "18) Biggest leaks", NV(cf.Leaks)));
            x.Item().Element(k => KV(k, "19) Budget system", NV(cf.BudgetSystem)));
            x.Item().Element(k => KV(k, "Emergency Fund Amount", Money(cf.EmergencyFundAmount)));
            x.Item().Element(k => KV(k, "Emergency Fund Months", NV(cf.EmergencyFundMonths)));
            x.Item().Element(k => KV(k, "21) Unexpected $10k plan", NV(cf.Unexpected10kPlan)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Debt & Obligations").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Home Value (Approx.)", Money(debt.HomeValue)));
            x.Item().Element(k => KV(k, "Mortgage Balance", Money(debt.MortgageBalance)));
            x.Item().Element(k => KV(k, "Interest Rate", NV(debt.MortgageRate)));
            x.Item().Element(k => KV(k, "Monthly Mortgage Payment", Money(debt.MortgagePayment)));
            x.Item().Element(k => KV(k, "Years Remaining", debt.MortgageYearsRemaining?.ToString() ?? "-"));

            x.Item().PaddingTop(6).Text("Other Debts").SemiBold().FontColor(GoldDark);
            otherDebtTable(x, debt.Other);

            x.Item().Element(k => KV(k, "22) Debt feels like", NV(debt.FeelsLike)));
            x.Item().Element(k => KV(k, "23) Change one thing about debt", NV(debt.ChangeOneThing)));

            static void otherDebtTable(ColumnDescriptor x, System.Collections.Generic.List<MiddleOtherDebt> items)
            {
                items ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Type");
                        header(h.Cell(), "Balance");
                        header(h.Cell(), "Rate");
                        header(h.Cell(), "Payment");
                        header(h.Cell(), "Notes");
                    });

                    foreach (var o in items)
                    {
                        cell(t.Cell(), NV(o.Type));
                        cell(t.Cell(), Money(o.Balance));
                        cell(t.Cell(), NV(o.Rate));
                        cell(t.Cell(), Money(o.Payment));
                        cell(t.Cell(), NV(o.Notes));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));

    // =========================================================
    // PART 6 — ASSETS + TAX + COLLEGE + LEGACY + DISCLAIMER (STEP 6)
    // =========================================================
    col.Item().Element(c => Card(c, "Part 6 — Assets, Tax, Legacy & Next Steps", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Retirement & Investment Accounts").SemiBold().FontColor(GoldDark);

            x.Item().PaddingTop(2).Element(k => KV(k, "24) Accounts organization feel", NV(assets.OrganizationFeel)));
            x.Item().Element(k => KV(k, "25) Net worth estimate", NV(assets.NetWorthEstimate)));

            x.Item().PaddingTop(6).Text("Retirement Accounts").SemiBold().FontColor(GoldDark);
            retirementTable(x, assets.Retirement);

            x.Item().PaddingTop(6).Text("Brokerage / Investment Accounts").SemiBold().FontColor(GoldDark);
            brokerageTable(x, assets.Brokerage);

            x.Item().PaddingTop(6).Text("Real Estate (excluding primary)").SemiBold().FontColor(GoldDark);
            realEstateTable(x, m.RealEstate);

            x.Item().PaddingTop(8).Element(Divider);

            x.Item().Text("Business Ownership").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Own a business? % owned", NV(biz.Ownership)));
            x.Item().Element(k => KV(k, "Approx. business value", Money(biz.Value)));
            x.Item().Element(k => KV(k, "26) Business continuity", NV(biz.Continuity)));
            x.Item().Element(k => KV(k, "27) Buy-sell / key person / succession", NV(biz.BuySellKeyPersonSuccession)));

            x.Item().PaddingTop(8).Element(Divider);

            x.Item().Text("Equity Compensation").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Receive RSUs/Options/Equity?", NV(eq.HasEquityComp)));
            x.Item().Element(k => KV(k, "Value / vesting notes", NV(eq.ValueNotes)));
            x.Item().Element(k => KV(k, "28) Equity tax/timing strategy", NV(eq.Strategy)));

            x.Item().PaddingTop(8).Element(Divider);

            x.Item().Text("Tax & Education Planning").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "29) Taxes feel too high why", NV(tax.FeelsTooHighWhy)));
            x.Item().Element(k => KV(k, "Tax preparer", NV(tax.Preparer)));
            x.Item().Element(k => KV(k, "Proactive planning last 12 months", NV(tax.ProactivePlanningLast12Mo)));
            x.Item().Element(k => KV(k, "30) Optimize most", NV(tax.OptimizeMost)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("College / Education Funding").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Plan to fund college", NV(college.PlanToFund)));
            x.Item().Element(k => KV(k, "Current accounts", NV(college.CurrentAccounts)));
            x.Item().Element(k => KV(k, "31) Plan if tuition higher", NV(college.PlanIfHigher)));

            x.Item().PaddingTop(8).Element(Divider);

            x.Item().Text("Legacy, Documents & Preparedness").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Has Will", NV(legacy.HasWill)));
            x.Item().Element(k => KV(k, "Has Trust", NV(legacy.HasTrust)));
            x.Item().Element(k => KV(k, "Power of Attorney (Financial)", NV(legacy.HasPOAFinancial)));
            x.Item().Element(k => KV(k, "Health Care Directive", NV(legacy.HasHealthDirective)));
            x.Item().Element(k => KV(k, "32) Last updated", NV(legacy.LastUpdated)));
            x.Item().Element(k => KV(k, "33) Family would find everything", NV(legacy.FamilyWouldFindEverything)));

            x.Item().PaddingTop(8).Element(Divider);

            x.Item().Text("Disclaimer").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Acknowledged Disclaimer", Bool(m.AcknowledgedDisclaimer)));

            static void retirementTable(ColumnDescriptor x, System.Collections.Generic.List<MiddleRetirementAccount> items)
            {
                items ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Type");
                        header(h.Cell(), "Institution");
                        header(h.Cell(), "Value");
                        header(h.Cell(), "Monthly Contrib");
                    });

                    foreach (var r in items)
                    {
                        cell(t.Cell(), NV(r.Type));
                        cell(t.Cell(), NV(r.Institution));
                        cell(t.Cell(), Money(r.Value));
                        cell(t.Cell(), Money(r.MonthlyContribution));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }

            static void brokerageTable(ColumnDescriptor x, System.Collections.Generic.List<MiddleBrokerageAccount> items)
            {
                items ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(3);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(4);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Institution");
                        header(h.Cell(), "Value");
                        header(h.Cell(), "Notes");
                    });

                    foreach (var b in items)
                    {
                        cell(t.Cell(), NV(b.Institution));
                        cell(t.Cell(), Money(b.Value));
                        cell(t.Cell(), NV(b.Notes));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }

            static void realEstateTable(ColumnDescriptor x, System.Collections.Generic.List<MiddleRealEstateItem> items)
            {
                items ??= new();

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Type");
                        header(h.Cell(), "Location");
                        header(h.Cell(), "Value");
                        header(h.Cell(), "Mortgage");
                        header(h.Cell(), "Cash Flow");
                        header(h.Cell(), "Notes");
                    });

                    foreach (var re in items)
                    {
                        cell(t.Cell(), NV(re.Type));
                        cell(t.Cell(), NV(re.Location));
                        cell(t.Cell(), Money(re.Value));
                        cell(t.Cell(), Money(re.Mortgage));
                        cell(t.Cell(), NV(re.CashFlow));
                        cell(t.Cell(), NV(re.Notes));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));
}

// =========================================================
// YOUNG: FULL (matches YoungFactFinder model + Younger.cshtml)
// =========================================================
private static void RenderYoung(ColumnDescriptor col, YoungFactFinder y)
{
    // PART 1 — Life + Direction + Work (+ Benefits)
    col.Item().Element(c => Card(c, "Part 1 — Life + Direction + Work", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Person").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Name", NV(y.Person.Name)));
            x.Item().Element(k => KV(k, "DOB", D(y.Person.DOB)));
            x.Item().Element(k => KV(k, "Location", NV(y.Person.Location)));
            x.Item().Element(k => KV(k, "Relationship Status", NV(y.Person.RelationshipStatus)));
            x.Item().Element(k => KV(k, "Has Dependents", NV(y.Person.HasDependents)));
            x.Item().Element(k => KV(k, "Dependents Count", y.Person.DependentsCount?.ToString() ?? "-"));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Direction").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "3–5 Year Vision", NV(y.Direction.ThreeToFiveYears)));
            x.Item().Element(k => KV(k, "10 Year Vision", NV(y.Direction.TenYears)));
            x.Item().Element(k => KV(k, "Current Stress", NV(y.Direction.CurrentStress)));
            x.Item().Element(k => KV(k, "Fear if no change", NV(y.Direction.FearIfNoChange)));
            x.Item().Element(k => KV(k, "One thing first", NV(y.Direction.OneThingFirst)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Ownership (Before the numbers)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Financial stability means", NV(y.Ownership.StabilityMeaning)));
            x.Item().Element(k => KV(k, "What stopped you", NV(y.Ownership.WhatsStoppedYou)));
            x.Item().Element(k => KV(k, "Worth it looks like", NV(y.Ownership.WorthIt)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Work").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Role", NV(y.Work.Role)));
            x.Item().Element(k => KV(k, "Employer", NV(y.Work.Employer)));
            x.Item().Element(k => KV(k, "Tenure", NV(y.Work.Tenure)));
            x.Item().Element(k => KV(k, "Income Type", NV(y.Work.IncomeType)));
            x.Item().Element(k => KV(k, "Annual Income", Money(y.Work.AnnualIncome)));
            x.Item().Element(k => KV(k, "Income Stability", NV(y.Work.IncomeStability)));
            x.Item().Element(k => KV(k, "Obstacle to increase income", NV(y.Work.ObstacleToIncreaseIncome)));
            x.Item().Element(k => KV(k, "Path exploration", NV(y.Work.PathExploration)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Benefits").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Health", Bool(y.Benefits.Health)));
            x.Item().Element(k => KV(k, "Dental", Bool(y.Benefits.Dental)));
            x.Item().Element(k => KV(k, "Vision", Bool(y.Benefits.Vision)));
            x.Item().Element(k => KV(k, "Retirement Plan", Bool(y.Benefits.RetirementPlan)));
            x.Item().Element(k => KV(k, "HSA", Bool(y.Benefits.HSA)));
            x.Item().Element(k => KV(k, "Group Life", Bool(y.Benefits.GroupLife)));
            x.Item().Element(k => KV(k, "Group DI", Bool(y.Benefits.GroupDI)));
            x.Item().Element(k => KV(k, "Understand Benefits", NV(y.Benefits.UnderstandBenefits)));
        });
    }));

    // PART 2 — Work + System + Habits (Cashflow/Banking/Emergency/Habits)
    col.Item().Element(c => Card(c, "Part 2 — Work + System + Emergency + Habits", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            var cf = y.Cashflow;
            x.Item().Text("Cashflow").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "System Level", NV(cf.SystemLevel)));
            x.Item().Element(k => KV(k, "Off track because", NV(cf.OffTrack)));
            x.Item().Element(k => KV(k, "Desired outcome", NV(cf.SystemDesiredOutcome)));
            x.Item().Element(k => KV(k, "Spending weakness", NV(cf.SpendingWeakness)));

            x.Item().PaddingTop(4).Element(k => KV(k, "Net Income (Monthly)", Money(cf.NetIncome)));
            x.Item().Element(k => KV(k, "Fixed Bills (Monthly)", Money(cf.FixedBills)));
            x.Item().Element(k => KV(k, "Variable Spending (Monthly)", Money(cf.VariableSpending)));
            x.Item().Element(k => KV(k, "Monthly Savings", Money(cf.MonthlySavings)));
            x.Item().Element(k => KV(k, "Monthly Debt Payments", Money(cf.DebtPayments)));
            x.Item().Element(k => KV(k, "End of Month", NV(cf.EndOfMonth)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Banking").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Know where money goes?", NV(y.Banking.KnowWhereMoneyGoes)));
            x.Item().Element(k => KV(k, "Number of accounts", y.Banking.NumAccounts?.ToString() ?? "-"));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Emergency").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Emergency Amount", Money(y.Emergency.Amount)));
            x.Item().Element(k => KV(k, "Months Covered", NV(y.Emergency.Months)));
            x.Item().Element(k => KV(k, "Plan if income stops", NV(y.Emergency.PlanIfIncomeStops)));
            x.Item().Element(k => KV(k, "Use credit to survive?", NV(y.Emergency.UsesCreditToSurvive)));
            x.Item().Element(k => KV(k, "Unexpected $1k plan", NV(y.Emergency.Unexpected1k)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Habits").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Need but not locked", NV(y.Habits.NeedButNotLocked)));
        });
    }));

    // PART 3 — Debt + Credit + Housing + Lifestyle + Health + Protection
    col.Item().Element(c => Card(c, "Part 3 — Debt + Credit + Housing + Lifestyle + Health + Protection", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Debt Items").SemiBold().FontColor(GoldDark);
            youngDebtTable(x, y.Debt);

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Debt Narrative").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Payoff Plan", NV(y.DebtNarrative.PayoffPlan)));
            x.Item().Element(k => KV(k, "Most Stressful", NV(y.DebtNarrative.MostStressful)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Credit").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Knows score range?", NV(y.Credit.KnowsScoreRange)));
            x.Item().Element(k => KV(k, "Late payments", NV(y.Credit.LatePayments)));
            x.Item().Element(k => KV(k, "Next major purchase", NV(y.Credit.NextMajorPurchase)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Housing").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Plan buy home?", NV(y.Housing.PlanBuyHome)));
            x.Item().Element(k => KV(k, "Timeline & range", NV(y.Housing.TimelineAndRange)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Lifestyle").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Protect from stress", NV(y.Lifestyle.ProtectFromStress)));
            x.Item().Element(k => KV(k, "Influences", NV(y.Lifestyle.Influences)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Life Transitions").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Transitions", NV(y.LifeTransitions)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Health").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Coverage", NV(y.Health.Coverage)));
            x.Item().Element(k => KV(k, "Deductible", NV(y.Health.Deductible)));
            x.Item().Element(k => KV(k, "OOP Max", NV(y.Health.OOPMax)));
            x.Item().Element(k => KV(k, "Bills setback", NV(y.Health.MedicalBillsSetback)));
            x.Item().Element(k => KV(k, "How pay bills if event", NV(y.Health.HowPayBillsIfHealthEvent)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Protection").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Has Life?", NV(y.Protection.HasLife)));
            x.Item().Element(k => KV(k, "Life Amount", NV(y.Protection.LifeAmount)));
            x.Item().Element(k => KV(k, "Who impacted if die", NV(y.Protection.WhoImpactedIfDie)));
            x.Item().Element(k => KV(k, "Has DI?", NV(y.Protection.HasDI)));
            x.Item().Element(k => KV(k, "If cannot work 3 mo", NV(y.Protection.IfCannotWork3Mo)));
            x.Item().Element(k => KV(k, "Belief about insurance", NV(y.Protection.BeliefAboutInsurance)));
            x.Item().Element(k => KV(k, "Protect first", NV(y.Protection.ProtectFirst)));

            static void youngDebtTable(ColumnDescriptor x, List<YoungDebtItem> items)
            {
                items ??= new();

                // If user never added rows (or EnsureDefaults didn't add),
                // render an empty single row so PDF still shows the headers.
                if (items.Count == 0) items.Add(new YoungDebtItem());

                x.Item().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(3);
                    });

                    t.Header(h =>
                    {
                        header(h.Cell(), "Type");
                        header(h.Cell(), "Balance");
                        header(h.Cell(), "Rate");
                        header(h.Cell(), "Payment");
                        header(h.Cell(), "Notes");
                    });

                    foreach (var d in items)
                    {
                        cell(t.Cell(), NV(d.Type));
                        cell(t.Cell(), Money(d.Balance));
                        cell(t.Cell(), NV(d.Rate));
                        cell(t.Cell(), Money(d.Payment));
                        cell(t.Cell(), NV(d.Notes));
                    }

                    static void header(IContainer c, string text) =>
                        c.Padding(6).Background(Gold).Text(text).FontColor(Colors.White).SemiBold().FontSize(9);

                    static void cell(IContainer c, string text) =>
                        c.Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Text(text).FontSize(9);
                });
            }
        });
    }));

    // PART 4 — Savings + Investing + Ownership (Action)
    col.Item().Element(c => Card(c, "Part 4 — Savings + Investing + Ownership", body =>
    {
        body.Column(x =>
        {
            x.Spacing(6);

            x.Item().Text("Savings").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Saves consistently?", NV(y.Savings.SavesConsistently)));
            x.Item().Element(k => KV(k, "Where", NV(y.Savings.Where)));
            x.Item().Element(k => KV(k, "Total Saved", Money(y.Savings.TotalSaved)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Investing").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Current investing", NV(y.Investing.CurrentInvesting)));
            x.Item().Element(k => KV(k, "Mistakes", NV(y.Investing.Mistakes)));
            x.Item().Element(k => KV(k, "Behind / on track", NV(y.Investing.FeelBehindOrOnTrack)));

            x.Item().PaddingTop(6).Element(Divider);

            x.Item().Text("Ownership (Action)").SemiBold().FontColor(GoldDark);
            x.Item().Element(k => KV(k, "Commitment scale", NV(y.Ownership.CommitmentScale)));
            x.Item().Element(k => KV(k, "First 30 days", NV(y.Ownership.First30Days)));
            x.Item().Element(k => KV(k, "Disclaimer acknowledged", Bool(y.AcknowledgedDisclaimer)));
        });
    }));
}

        // =========================================================
        // MODEL STATE NORMALIZATION (critical)
        // =========================================================
        private void NormalizeModelState(FactFinderViewModel model)
        {
            void RemovePrefix(string prefix)
            {
                var keys = ModelState.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var k in keys) ModelState.Remove(k);
            }

            var ft = (model.FormType ?? "").Trim();

            if (ft.Equals("Senior", StringComparison.OrdinalIgnoreCase))
            {
                RemovePrefix("Middle.");
                RemovePrefix("Young.");
            }
            else if (ft.Equals("Middle", StringComparison.OrdinalIgnoreCase))
            {
                RemovePrefix("Senior.");
                RemovePrefix("Young.");
            }
            else if (ft.Equals("Young", StringComparison.OrdinalIgnoreCase))
            {
                RemovePrefix("Senior.");
                RemovePrefix("Middle.");
            }
            else
            {
                // If FormType is missing, don't let hidden sections block download
                RemovePrefix("Senior.");
                RemovePrefix("Middle.");
                RemovePrefix("Young.");
            }
        }

        // =========================================================
        // DEFAULTS (prevent null crashes - LISTS + NESTED OBJECTS)
        // =========================================================
        private static void EnsureDefaults(FactFinderViewModel m)
        {
            m ??= new FactFinderViewModel();

            // Root objects
            m.Senior ??= new SeniorFactFinder();
            m.Middle ??= new MiddleFactFinder();
            m.Young  ??= new YoungFactFinder();

            // -----------------------------
            // SENIOR: nested safety
            // -----------------------------
            m.Senior.Applicant ??= new SeniorApplicant();
            m.Senior.Spouse ??= new SeniorSpouse();
            m.Senior.EmergencyContact ??= new SeniorEmergencyContact();

            m.Senior.Medical ??= new SeniorMedical();
            m.Senior.Medical.ApplicantCoverage ??= new SeniorMedicalCoverage();
            m.Senior.Medical.SpouseCoverage ??= new SeniorMedicalCoverage();
            m.Senior.Medical.ApplicantParents ??= new();
            m.Senior.Medical.SpouseParents ??= new();

            m.Senior.ExtendedCare ??= new SeniorExtendedCare();
            m.Senior.ExtendedCare.CurrentPolicy ??= new SeniorExtendedCarePolicy();

            m.Senior.LifeInsurance ??= new SeniorLifeInsurance();
            m.Senior.LifeInsurance.ApplicantPolicies ??= new();
            m.Senior.LifeInsurance.SpousePolicies ??= new();

            m.Senior.Retirement ??= new SeniorRetirement();
            m.Senior.Retirement.ApplicantIncome ??= new SeniorIncomeGroup();
            m.Senior.Retirement.SpouseIncome ??= new SeniorIncomeGroup();
            m.Senior.Retirement.NonLiquidAssets ??= new SeniorNonLiquidAssets();
            m.Senior.Retirement.LiquidAssets ??= new SeniorLiquidAssets();
            m.Senior.Retirement.K401 ??= new Senior401kGroup();
            m.Senior.Retirement.CDs ??= new();
            m.Senior.Retirement.AnnuitiesIRAs ??= new();
            m.Senior.Retirement.OtherAssets ??= new();

            // Senior lists sizing
            m.Senior.Children ??= new();
            while (m.Senior.Children.Count < 4)
                m.Senior.Children.Add(new SeniorChild());

            if (m.Senior.Medical.ApplicantParents.Count < 2)
            {
                m.Senior.Medical.ApplicantParents = new()
                {
                    new SeniorParentInfo { Label = "Applicant Father" },
                    new SeniorParentInfo { Label = "Applicant Mother" }
                };
            }

            if (m.Senior.Medical.SpouseParents.Count < 2)
            {
                m.Senior.Medical.SpouseParents = new()
                {
                    new SeniorParentInfo { Label = "Spouse Father" },
                    new SeniorParentInfo { Label = "Spouse Mother" }
                };
            }

            while (m.Senior.LifeInsurance.ApplicantPolicies.Count < 3)
                m.Senior.LifeInsurance.ApplicantPolicies.Add(
                    new SeniorLifePolicy { Label = $"Policy {m.Senior.LifeInsurance.ApplicantPolicies.Count + 1}" });

            while (m.Senior.LifeInsurance.SpousePolicies.Count < 3)
                m.Senior.LifeInsurance.SpousePolicies.Add(
                    new SeniorLifePolicy { Label = $"Policy {m.Senior.LifeInsurance.SpousePolicies.Count + 1}" });

            while (m.Senior.Retirement.CDs.Count < 3)
                m.Senior.Retirement.CDs.Add(new SeniorCDHolding());

            while (m.Senior.Retirement.AnnuitiesIRAs.Count < 3)
                m.Senior.Retirement.AnnuitiesIRAs.Add(new SeniorAnnuityIraHolding());

            while (m.Senior.Retirement.OtherAssets.Count < 2)
                m.Senior.Retirement.OtherAssets.Add(new SeniorOtherAsset());

            // -----------------------------
            // MIDDLE: nested safety (THIS is what your current code is missing)
            // -----------------------------
            m.Middle.Applicant ??= new MiddleApplicant();
            m.Middle.ApplicantBenefits ??= new MiddleEmployerBenefits();
            m.Middle.Spouse ??= new MiddleSpouse();
            m.Middle.Dependents ??= new();
            m.Middle.EmergencyContact ??= new MiddleEmergencyContact();

            m.Middle.Health ??= new MiddleHealth();
            m.Middle.Health.Additional ??= new MiddleHealthAdditional();

            m.Middle.Life ??= new MiddleLife();
            m.Middle.Life.Applicant ??= new();
            m.Middle.Life.Spouse ??= new();

            m.Middle.DI ??= new MiddleDI();
            m.Middle.Liability ??= new MiddleLiability();

            m.Middle.Cashflow ??= new MiddleCashflow();
            m.Middle.Debt ??= new MiddleDebt();
            m.Middle.Debt.Other ??= new();

            m.Middle.Assets ??= new MiddleAssets();
            m.Middle.Assets.Retirement ??= new();
            m.Middle.Assets.Brokerage ??= new();

            m.Middle.RealEstate ??= new();

            m.Middle.Business ??= new MiddleBusiness();
            m.Middle.EquityComp ??= new MiddleEquityComp();
            m.Middle.Tax ??= new MiddleTax();
            m.Middle.College ??= new MiddleCollege();
            m.Middle.Legacy ??= new MiddleLegacy();
            m.Middle.Direction ??= new MiddleDirection();

            // -----------------------------
            // YOUNG: nested safety (THIS is what your current code is missing)
            // -----------------------------
            m.Young.Person ??= new YoungPerson();
            m.Young.Direction ??= new YoungDirection();
            m.Young.Work ??= new YoungWork();
            m.Young.Benefits ??= new YoungBenefits();

            m.Young.Cashflow ??= new YoungCashflow();
            m.Young.Banking ??= new YoungBanking();
            m.Young.Emergency ??= new YoungEmergency();
            m.Young.Habits ??= new YoungHabits();

            m.Young.Debt ??= new();
            m.Young.DebtNarrative ??= new YoungDebtNarrative();
            m.Young.Credit ??= new YoungCredit();
            m.Young.Housing ??= new YoungHousing();
            m.Young.Lifestyle ??= new YoungLifestyle();
            m.Young.Health ??= new YoungHealth();
            m.Young.Protection ??= new YoungProtection();

            m.Young.Savings ??= new YoungSavings();
            m.Young.Investing ??= new YoungInvesting();
            m.Young.Ownership ??= new YoungOwnership();
        }

        // =========================================================
        // NAME + FILE SAFE
        // =========================================================
        private static string GetClientName(FactFinderViewModel m)
        {
            var ft = (m.FormType ?? "").Trim();

            return ft switch
            {
                "Senior" => m.Senior?.Applicant?.ApplicantName ?? "Client",
                "Middle" => m.Middle?.Applicant?.Name ?? "Client",
                "Young"  => m.Young?.Person?.Name ?? "Client",
                _        => "Client"
            };
        }

        private static string Sanitize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Client";

            var invalid = Path.GetInvalidFileNameChars();
            return new string(input.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
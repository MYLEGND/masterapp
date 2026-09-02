(function (root, factory) {
    const api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    root.LegendExpenseLensProjection = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
    "use strict";

    const CURRENT_VERSION = 2;
    const MAX_PROJECTION_MONTHS = 120;
    const MAX_MOBILE_PERIOD_MONTHS = 24;

    const normalizeFrequency = (value) => {
        const normalized = (value || "").toString().toLowerCase().replace(/[^a-z]/g, "");
        if (normalized === "weekly") return "weekly";
        if (normalized === "biweekly") return "biweekly";
        return "monthly";
    };

    const parseMoneyToCents = (value) => {
        if (typeof value === "number" && Number.isFinite(value)) {
            return Math.round(value * 100);
        }

        const normalized = String(value ?? "")
            .replace(/[,$\s]/g, "")
            .trim();
        if (!normalized) return 0;
        const parsed = Number.parseFloat(normalized);
        if (!Number.isFinite(parsed)) return 0;
        return Math.round(parsed * 100);
    };

    const parseStoredCentsOrMoney = (centsValue, moneyValue = 0) => {
        if (
            centsValue !== undefined
            && centsValue !== null
            && centsValue !== ""
        ) {
            const normalized = typeof centsValue === "number"
                ? centsValue
                : Number.parseFloat(
                    String(centsValue)
                        .replace(/[,$\s]/g, "")
                        .trim()
                );

            return Number.isFinite(normalized)
                ? Math.round(normalized)
                : 0;
        }

        return parseMoneyToCents(moneyValue);
    };

    const centsToDollars = (value) => {
        const cents = Number.isFinite(value) ? value : 0;
        return cents / 100;
    };

    const clampCurrencyFloor = (value) => Math.max(0, Math.round(value || 0));

    const pad2 = (value) => String(value).padStart(2, "0");

    const formatDateKey = (date) => {
        if (!(date instanceof Date) || Number.isNaN(date.getTime())) return "";
        return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
    };

    const parseDate = (value) => {
        const raw = String(value || "").trim();
        if (!raw) return null;
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(raw);
        if (!match) return null;
        const year = Number.parseInt(match[1], 10);
        const month = Number.parseInt(match[2], 10);
        const day = Number.parseInt(match[3], 10);
        if (!Number.isFinite(year) || !Number.isFinite(month) || !Number.isFinite(day)) return null;
        const date = new Date(year, month - 1, day);
        if (
            date.getFullYear() !== year ||
            date.getMonth() !== month - 1 ||
            date.getDate() !== day
        ) {
            return null;
        }
        date.setHours(0, 0, 0, 0);
        return date;
    };

    const todayDate = (value) => {
        const date = value instanceof Date ? new Date(value) : new Date();
        date.setHours(0, 0, 0, 0);
        return date;
    };

    const formatMonthKey = (value) => {
        if (typeof value === "string") {
            const match = /^(\d{4})-(\d{2})$/.exec(value.trim());
            if (match) return `${match[1]}-${match[2]}`;
        }

        const date = value instanceof Date ? value : parseDate(value);
        if (!date) return "";
        return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}`;
    };

    const parseMonthKey = (value) => {
        const match = /^(\d{4})-(\d{2})$/.exec(String(value || "").trim());
        if (!match) return null;
        const year = Number.parseInt(match[1], 10);
        const month = Number.parseInt(match[2], 10);
        if (!Number.isFinite(year) || !Number.isFinite(month) || month < 1 || month > 12) return null;
        return new Date(year, month - 1, 1);
    };

    const compareMonthKeys = (left, right) => {
        const leftDate = parseMonthKey(left);
        const rightDate = parseMonthKey(right);
        if (!leftDate && !rightDate) return 0;
        if (!leftDate) return -1;
        if (!rightDate) return 1;
        return leftDate.getTime() - rightDate.getTime();
    };

    const addMonths = (monthKey, offset) => {
        const base = parseMonthKey(monthKey);
        if (!base) return "";
        const next = new Date(base.getFullYear(), base.getMonth() + offset, 1);
        return formatMonthKey(next);
    };

    const getMonthContext = (monthKey) => {
        const monthDate = parseMonthKey(monthKey);
        const resolved = monthDate || todayDate();
        const year = resolved.getFullYear();
        const month = resolved.getMonth();
        const days = new Date(year, month + 1, 0).getDate();
        return {
            year,
            month,
            days,
            monthKey: formatMonthKey(resolved),
            startDate: new Date(year, month, 1),
            endDate: new Date(year, month, days, 23, 59, 59, 999)
        };
    };

    const getDefaultAnchorDate = (options = {}) => {
        const monthKey = formatMonthKey(options.monthKey || options.now || new Date());
        const monthContext = getMonthContext(monthKey);
        return `${monthContext.year}-${pad2(monthContext.month + 1)}-01`;
    };

    const normalizeDayOfMonth = (value, fallback = null) => {
        if (value === undefined || value === null || value === "") return fallback;
        const parsed = Number.parseInt(String(value).trim(), 10);
        if (!Number.isFinite(parsed)) return fallback;
        return Math.min(31, Math.max(1, parsed));
    };

    const buildMonthDayDate = (monthKey, dayOfMonth) => {
        const monthContext = getMonthContext(monthKey);
        const clampedDay = Math.min(
            monthContext.days,
            Math.max(1, normalizeDayOfMonth(dayOfMonth, 1))
        );
        const date = new Date(monthContext.year, monthContext.month, clampedDay);
        date.setHours(0, 0, 0, 0);
        return date;
    };

    const getScheduledOccurrenceDays = (anchorValue, frequencyValue, options = {}) => {
        const anchorDate = parseDate(anchorValue);
        if (!anchorDate) return [];

        const frequency = normalizeFrequency(frequencyValue);
        const monthKey = formatMonthKey(options.monthKey || options.now || new Date());
        const monthContext = getMonthContext(monthKey);
        const week = options.week || null;
        const rangeStart = week ? new Date(week.startDate) : new Date(monthContext.startDate);
        const rangeEnd = week ? new Date(week.endDate) : new Date(monthContext.endDate);
        rangeStart.setHours(0, 0, 0, 0);
        rangeEnd.setHours(23, 59, 59, 999);

        const occurrences = [];
        const cursor = new Date(anchorDate);

        if (frequency === "monthly") {
            const anchorDay = anchorDate.getDate();
            const monthDays = new Date(monthContext.year, monthContext.month + 1, 0).getDate();
            cursor.setFullYear(monthContext.year, monthContext.month, Math.min(anchorDay, monthDays));
            cursor.setHours(0, 0, 0, 0);
            if (cursor >= anchorDate && cursor >= rangeStart && cursor <= rangeEnd) {
                occurrences.push(new Date(cursor));
            }
            return occurrences;
        }

        const intervalDays = frequency === "biweekly" ? 14 : 7;
        const msPerDay = 86400000;
        const daysFromAnchorToRangeStart = Math.floor(
            (Date.UTC(rangeStart.getFullYear(), rangeStart.getMonth(), rangeStart.getDate()) -
                Date.UTC(anchorDate.getFullYear(), anchorDate.getMonth(), anchorDate.getDate())) /
                msPerDay
        );

        if (daysFromAnchorToRangeStart > 0) {
            const intervalsToAdvance = Math.ceil(daysFromAnchorToRangeStart / intervalDays);
            cursor.setDate(cursor.getDate() + intervalsToAdvance * intervalDays);
        }

        while (cursor <= rangeEnd) {
            if (cursor >= rangeStart && cursor >= anchorDate) {
                occurrences.push(new Date(cursor));
            }
            cursor.setDate(cursor.getDate() + intervalDays);
        }

        return occurrences;
    };

    const getCalendarWeeksForMonth = (monthKey) => {
        const monthContext = getMonthContext(monthKey);
        const weeks = [];
        let startDayOfMonth = 1;
        let weekNumber = 1;

        while (startDayOfMonth <= monthContext.days) {
            const weekStart = new Date(monthContext.year, monthContext.month, startDayOfMonth);
            weekStart.setHours(0, 0, 0, 0);
            const endDayOfMonth = Math.min(startDayOfMonth + 6, monthContext.days);
            const weekEnd = new Date(monthContext.year, monthContext.month, endDayOfMonth);
            weekEnd.setHours(23, 59, 59, 999);

            weeks.push({
                id: `${monthContext.monthKey}-week-${weekNumber}`,
                label: `Week ${weekNumber}`,
                startDate: weekStart,
                endDate: weekEnd,
                monthKey,
                isCurrentMonthWeek: true
            });

            startDayOfMonth = endDayOfMonth + 1;
            weekNumber += 1;
        }

        return weeks;
    };

    const escapeKeyPart = (value) => String(value || "").replace(/[^a-zA-Z0-9_-]/g, "").trim();

    const buildOccurrenceKey = (kind, sourceId, dateKey, extra = "") => {
        return [kind, escapeKeyPart(sourceId), dateKey, escapeKeyPart(extra)].filter(Boolean).join(":");
    };

    const isTrackedDebtMinimumCategory = (value) =>
        String(value || "").trim().toLowerCase() === "tracked-unsecured-minimum";

    const isCreditPaymentMethod = (value) =>
        String(value || "").trim().toLowerCase() === "credit";

    const normalizeDebtCategory = (name, explicitValue) => {
        const explicit = String(explicitValue || "").trim().toLowerCase();
        if (explicit === "tracked-unsecured-minimum" || explicit === "external-debt-obligation" || explicit === "none") {
            return explicit;
        }

        const normalizedName = String(name || "").trim().toLowerCase();
        if (!normalizedName) return "none";

        if (
            normalizedName.includes("debt payment - credit") ||
            normalizedName.includes("credit card payment") ||
            normalizedName.includes("credit cards") ||
            normalizedName.includes("personal loan")
        ) {
            return "tracked-unsecured-minimum";
        }

        if (
            normalizedName.includes("student loan") ||
            normalizedName.includes("mortgage") ||
            normalizedName.includes("auto payment") ||
            normalizedName.includes("business loan")
        ) {
            return "external-debt-obligation";
        }

        return "none";
    };

    const getEventExecutionOrder = (eventItem) => {
        if (eventItem?.kind === "income") return 0;

        if (eventItem?.kind === "expense") {
            if (isTrackedDebtMinimumCategory(eventItem.debtCategory)) return 3;
            return isCreditPaymentMethod(eventItem.paymentMethod) ? 2 : 1;
        }

        if (eventItem?.kind === "debtAdjustment") return 4;
        if (eventItem?.kind === "extraDebt") return 5;
        return 9;
    };

    const sortProjectedEvents = (left, right) => {
        const dateDiff = left.date.getTime() - right.date.getTime();
        if (dateDiff !== 0) return dateDiff;

        const orderDiff = getEventExecutionOrder(left) - getEventExecutionOrder(right);
        if (orderDiff !== 0) return orderDiff;

        return left.label.localeCompare(right.label);
    };

    const inferCreditPaymentDayOfMonth = (categories = []) => {
        const uniqueDays = new Set();

        categories.forEach((category) => {
            if (!isCreditPaymentMethod(category?.paymentMethod)) return;
            const parsedDueDate = parseDate(category?.due);
            if (!parsedDueDate) return;
            uniqueDays.add(parsedDueDate.getDate());
        });

        return uniqueDays.size === 1
            ? Array.from(uniqueDays)[0]
            : null;
    };

    const normalizeProjectionSettings = (rawProjectionSettings = {}, categories = []) => {
        const source = rawProjectionSettings && typeof rawProjectionSettings === "object"
            ? rawProjectionSettings
            : {};
        const hasExplicitCreditPaymentDay = Object.prototype.hasOwnProperty.call(
            source,
            "creditPaymentDayOfMonth"
        );

        return {
            protectedCashReserveCents: clampCurrencyFloor(
                parseStoredCentsOrMoney(
                    source?.protectedCashReserveCents,
                    source?.protectedCashReserve ?? 0
                )
            ),
            creditPaymentDayOfMonth: hasExplicitCreditPaymentDay
                ? normalizeDayOfMonth(source.creditPaymentDayOfMonth, null)
                : inferCreditPaymentDayOfMonth(categories)
        };
    };

    const normalizeIncomeStream = (groupKey, stream, index) => ({
        id: String(stream?.id || `${groupKey}-stream-${index + 1}`).trim(),
        label: String(stream?.label || "").trim(),
        amount: String(stream?.amount || "").trim(),
        frequency: normalizeFrequency(stream?.frequency || stream?.recurrence),
        anchorDate: formatDateKey(parseDate(stream?.anchorDate || stream?.date) || parseDate(getDefaultAnchorDate())) || getDefaultAnchorDate()
    });

    const normalizeIncomeStreams = (rawState) => {
        const rawGroups = rawState?.incomeStreams;
        const normalizeGroup = (groupKey, input) => {
            if (Array.isArray(input) && input.length > 0) {
                return input.map((stream, index) => normalizeIncomeStream(groupKey, stream, index));
            }
            return [normalizeIncomeStream(groupKey, {}, 0)];
        };

        if (rawGroups && typeof rawGroups === "object") {
            return {
                primary: normalizeGroup("primary", rawGroups.primary),
                secondary: Array.isArray(rawGroups.secondary) && rawGroups.secondary.length > 0
                    ? rawGroups.secondary.map((stream, index) => normalizeIncomeStream("secondary", stream, index))
                    : []
            };
        }

        const primary = [];
        const secondary = [];
        const primaryIncome = String(rawState?.primaryIncome ?? "").trim() || String(rawState?.income ?? "").trim();
        const spouseIncome = String(rawState?.spouseIncome ?? "").trim();

        if (primaryIncome) {
            primary.push(normalizeIncomeStream("primary", {
                amount: primaryIncome,
                frequency: "monthly",
                anchorDate: getDefaultAnchorDate()
            }, 0));
        } else {
            primary.push(normalizeIncomeStream("primary", {}, 0));
        }

        if (spouseIncome) {
            secondary.push(normalizeIncomeStream("secondary", {
                amount: spouseIncome,
                frequency: "monthly",
                anchorDate: getDefaultAnchorDate()
            }, 0));
        }

        return { primary, secondary };
    };

    const normalizeCategory = (category, index) => {
        const due = formatDateKey(parseDate(category?.due) || parseDate(getDefaultAnchorDate())) || getDefaultAnchorDate();
        const baseId = String(category?.id || "").trim();
        const fallbackId = `cat-${index + 1}-${escapeKeyPart(String(category?.name || "").toLowerCase()) || "expense"}`;
        const occurrenceAmount = String(category?.occurrenceAmount ?? "").trim();

        return {
            id: baseId || fallbackId,
            index: category?.index ?? index + 1,
            name: String(category?.name || "").trim(),
            due,
            frequency: normalizeFrequency(category?.frequency || category?.recurrence),
            paymentMethod: String(category?.paymentMethod || "").trim().toLowerCase() === "credit" ? "credit" : "debit",
            amount: occurrenceAmount || String(category?.amount ?? "").trim(),
            isTemplate: category?.isTemplate === true,
            isPinned: category?.isPinned === true,
            debtCategory: normalizeDebtCategory(category?.name, category?.debtCategory)
        };
    };

    const normalizeHistoryMap = (input = {}, fallbackType = "") => {
        const next = {};
        Object.entries(input || {}).forEach(([key, raw]) => {
            if (!raw || typeof raw !== "object") return;
            const dateKey = String(raw.dateKey || key.split(":").slice(-2, -1)[0] || key.split(":").slice(-1)[0] || "").trim();
            const status = String(raw.status || "").trim().toLowerCase() === "completed" ? "completed" : "pending";
            next[key] = {
                status,
                dateKey: /^\d{4}-\d{2}-\d{2}$/.test(dateKey) ? dateKey : "",
                actualAmountCents: parseStoredCentsOrMoney(
                    raw.actualAmountCents,
                    raw.actualAmount ?? 0
                ),
                completedAt: String(raw.completedAt || "").trim(),
                note: String(raw.note || "").trim(),
                label: String(raw.label || "").trim(),
                paymentMethod: String(raw.paymentMethod || "").trim().toLowerCase(),
                frequency: normalizeFrequency(raw.frequency || ""),
                sourceType: String(raw.sourceType || fallbackType || "").trim(),
                sourceId: String(raw.sourceId || "").trim()
            };
        });
        return next;
    };

    const normalizeDebtAdjustments = (input = []) => {
        if (!Array.isArray(input)) return [];
        return input
            .map((adjustment, index) => ({
                id: String(adjustment?.id || `adjustment-${index + 1}`).trim(),
                date: formatDateKey(parseDate(adjustment?.date) || todayDate()) || formatDateKey(todayDate()),
                amountCents: parseStoredCentsOrMoney(
                    adjustment?.amountCents,
                    adjustment?.amount ?? 0
                ),
                note: String(adjustment?.note || "").trim()
            }))
            .sort((left, right) => {
                if (left.date !== right.date) return left.date.localeCompare(right.date);
                return left.id.localeCompare(right.id);
            });
    };

    const normalizeDebtState = (rawDebt) => {
        const openingBalanceCents = clampCurrencyFloor(
            parseStoredCentsOrMoney(
                rawDebt?.openingBalanceCents,
                rawDebt?.openingBalance
                    ?? rawDebt?.balance
                    ?? rawDebt?.currentBalance
                    ?? 0
            )
        );

        const currentBalanceCents = clampCurrencyFloor(
            parseStoredCentsOrMoney(
                rawDebt?.currentBalanceCents,
                rawDebt?.currentBalance
                    ?? openingBalanceCents / 100
            )
        );

        const monthlyMinimumPaymentsCents = clampCurrencyFloor(
            parseStoredCentsOrMoney(
                rawDebt?.monthlyMinimumPaymentsCents,
                rawDebt?.monthlyMinimumPayments ?? 0
            )
        );
        const asOfDate = formatDateKey(parseDate(rawDebt?.asOfDate) || todayDate()) || formatDateKey(todayDate());

        return {
            openingBalanceCents,
            currentBalanceCents,
            asOfDate,
            monthlyMinimumPaymentsCents,
            projectedPayoffDate: rawDebt?.projectedPayoffDate ? String(rawDebt.projectedPayoffDate) : null,
            projectedInterestExcluded: rawDebt?.projectedInterestExcluded !== false,
            extraPaymentStrategy: String(rawDebt?.extraPaymentStrategy || "remaining-cash").trim() || "remaining-cash",
            paymentHistory: Array.isArray(rawDebt?.paymentHistory) ? rawDebt.paymentHistory : [],
            adjustments: normalizeDebtAdjustments(rawDebt?.adjustments)
        };
    };

    const normalizeMonthlyOverrides = (input = {}) => {
        const next = {};
        Object.entries(input || {}).forEach(([monthKey, raw]) => {
            const normalizedMonthKey = formatMonthKey(monthKey);
            if (!normalizedMonthKey) return;

            if (raw === null || raw === undefined || raw === "") return;

            const rawObject = typeof raw === "object" ? raw : { amount: raw };
            next[normalizedMonthKey] = {
                amountCents: parseStoredCentsOrMoney(
                    rawObject.amountCents,
                    rawObject.amount ?? 0
                ),
                note: String(rawObject.note || "").trim(),
                updatedAt: String(rawObject.updatedAt || "").trim()
            };
        });
        return next;
    };

    const normalizeState = (rawState, options = {}) => {
        const state = rawState && typeof rawState === "object" ? rawState : {};
        const sourceCategories = Array.isArray(state.categories) && state.categories.length > 0
            ? state.categories
            : Array.isArray(state.expenses)
                ? state.expenses
                : Array.isArray(state.categories)
                    ? state.categories
                    : [];
        const normalizedCategories = sourceCategories.map((category, index) => normalizeCategory(category, index));
        const normalizedDebt = normalizeDebtState(state.debt || {
            openingBalance: state.creditCardAndPersonalLoanDebt || 0
        });
        const normalizedState = {
            ...state,
            stateVersion: CURRENT_VERSION,
            incomeStreams: normalizeIncomeStreams(state),
            categories: normalizedCategories,
            debt: normalizedDebt,
            monthlyStartingBalanceOverrides: normalizeMonthlyOverrides(
                state.monthlyStartingBalanceOverrides ||
                state.monthOverrides ||
                state.startingBalanceOverrides
            ),
            occurrenceHistory: {
                incomes: normalizeHistoryMap(state?.occurrenceHistory?.incomes || state?.history?.incomes, "income"),
                expenses: normalizeHistoryMap(state?.occurrenceHistory?.expenses || state?.history?.expenses, "expense"),
                debtPayments: normalizeHistoryMap(state?.occurrenceHistory?.debtPayments || state?.history?.debtPayments, "debt")
            },
            projectionSettings: normalizeProjectionSettings(
                state?.projectionSettings,
                normalizedCategories
            )
        };

        if (options.includeComputedDebtBalance !== false) {
            normalizedState.debt.currentBalanceCents = clampCurrencyFloor(normalizedState.debt.currentBalanceCents || normalizedState.debt.openingBalanceCents);
        }

        return normalizedState;
    };

    const summarizeIncomeGroups = (groups, options = {}) => {
        const monthKey = options.monthKey || formatMonthKey(options.now || new Date());
        const week = options.week || null;
        const groupLabelMap = options.groupLabelMap || {};
        const groupTotals = { primary: 0, secondary: 0 };
        const hits = [];

        ["primary", "secondary"].forEach((groupKey) => {
            const streams = Array.isArray(groups?.[groupKey]) ? groups[groupKey] : [];
            const baseLabel = groupLabelMap[groupKey] || (groupKey === "secondary" ? "Partner Income" : "Income");

            streams.forEach((stream, index) => {
                const amountCents = parseMoneyToCents(stream?.amount);
                if (amountCents <= 0) return;

                const frequency = normalizeFrequency(stream?.frequency);
                const anchorDate = String(stream?.anchorDate || "").trim() || getDefaultAnchorDate({ monthKey });
                const label = String(stream?.label || "").trim() || (streams.length > 1 ? `${baseLabel} Stream ${index + 1}` : baseLabel);

                const normalizedMonthlyAmountCents = frequency === "weekly"
                    ? Math.round((amountCents * 52) / 12)
                    : frequency === "biweekly"
                        ? Math.round((amountCents * 26) / 12)
                        : amountCents;

                groupTotals[groupKey] += normalizedMonthlyAmountCents;

                getScheduledOccurrenceDays(anchorDate, frequency, { monthKey, week }).forEach((date) => {
                    hits.push({
                        id: stream?.id || `${groupKey}-stream-${index + 1}`,
                        groupKey,
                        label,
                        amountCents,
                        amount: centsToDollars(amountCents),
                        date,
                        dateKey: formatDateKey(date),
                        frequency,
                        anchorDate
                    });
                });
            });
        });

        hits.sort((left, right) => {
            const dateDiff = left.date.getTime() - right.date.getTime();
            if (dateDiff !== 0) return dateDiff;
            return left.label.localeCompare(right.label);
        });

        return {
            monthlyTotalCents: groupTotals.primary + groupTotals.secondary,
            monthlyTotal: centsToDollars(groupTotals.primary + groupTotals.secondary),
            groupTotalsCents: groupTotals,
            groupTotals: {
                primary: centsToDollars(groupTotals.primary),
                secondary: centsToDollars(groupTotals.secondary)
            },
            hits,
            count: hits.length
        };
    };

    const summarizeExpenseCategories = (rawState, options = {}) => {
        const state = normalizeState(rawState || {});
        const monthKey = options.monthKey || formatMonthKey(options.now || new Date());
        const scheduledTotalsBySourceId = new Map();

        buildScheduledExpenseOccurrences(state, monthKey).forEach((occurrence) => {
            const sourceId = String(occurrence.sourceId || "").trim();
            if (!sourceId) return;
            scheduledTotalsBySourceId.set(
                sourceId,
                (scheduledTotalsBySourceId.get(sourceId) || 0) + Math.max(0, Math.round(occurrence.amountCents || 0))
            );
        });

        const items = state.categories.map((category) => {
            const amountCents = Math.max(0, parseMoneyToCents(category.amount));
            const normalizedMonthlyAmountCents = category.frequency === "weekly"
                ? Math.round((amountCents * 52) / 12)
                : category.frequency === "biweekly"
                    ? Math.round((amountCents * 26) / 12)
                    : amountCents;
            const scheduledMonthAmountCents = scheduledTotalsBySourceId.get(category.id) || 0;

            return {
                id: category.id,
                name: category.name,
                frequency: category.frequency,
                paymentMethod: category.paymentMethod,
                debtCategory: category.debtCategory,
                amountCents,
                normalizedMonthlyAmountCents,
                scheduledMonthAmountCents
            };
        });

        const normalizedMonthlyTotalCents = items.reduce(
            (sum, item) => sum + item.normalizedMonthlyAmountCents,
            0
        );
        const scheduledMonthTotalCents = items.reduce(
            (sum, item) => sum + item.scheduledMonthAmountCents,
            0
        );

        return {
            monthKey,
            items,
            normalizedMonthlyTotalCents,
            normalizedMonthlyTotal: centsToDollars(normalizedMonthlyTotalCents),
            scheduledMonthTotalCents,
            scheduledMonthTotal: centsToDollars(scheduledMonthTotalCents)
        };
    };

    const buildScheduledIncomeOccurrences = (state, monthKey) => {
        const hits = summarizeIncomeGroups(state.incomeStreams, { monthKey }).hits;
        return hits.map((hit) => ({
            key: buildOccurrenceKey("income", `${hit.groupKey}-${hit.id}`, hit.dateKey),
            kind: "income",
            sourceType: "income",
            sourceId: `${hit.groupKey}-${hit.id}`,
            groupKey: hit.groupKey,
            label: hit.label,
            amountCents: hit.amountCents,
            date: hit.date,
            dateKey: hit.dateKey,
            frequency: hit.frequency,
            paymentMethod: "deposit"
        }));
    };

    const buildScheduledExpenseOccurrences = (state, monthKey) => {
        const occurrences = [];
        const monthContext = getMonthContext(monthKey);
        const creditPaymentDayOfMonth = normalizeDayOfMonth(
            state?.projectionSettings?.creditPaymentDayOfMonth,
            null
        );
        const creditPaymentDate = creditPaymentDayOfMonth
            ? buildMonthDayDate(monthKey, creditPaymentDayOfMonth)
            : null;
        const monthEndDate = buildMonthDayDate(monthKey, monthContext.days);

        state.categories.forEach((category) => {
            const amountCents = parseMoneyToCents(category.amount);
            if (amountCents <= 0) return;
            const due = String(category.due || "").trim();
            if (!due) return;

            getScheduledOccurrenceDays(due, category.frequency, { monthKey }).forEach((sourceDate) => {
                const sourceDateKey = formatDateKey(sourceDate);
                const tracksDebtMinimum = isTrackedDebtMinimumCategory(category.debtCategory);
                const scheduledDate = tracksDebtMinimum
                    ? monthEndDate
                    : isCreditPaymentMethod(category.paymentMethod) && creditPaymentDate
                        ? creditPaymentDate
                        : sourceDate;
                const scheduledDateKey = formatDateKey(scheduledDate);
                const legacyKey = buildOccurrenceKey("expense", category.id, sourceDateKey);
                const occurrenceKey = scheduledDateKey === sourceDateKey
                    ? legacyKey
                    : buildOccurrenceKey("expense", category.id, scheduledDateKey, sourceDateKey);
                occurrences.push({
                    key: occurrenceKey,
                    legacyKeys: legacyKey === occurrenceKey ? [] : [legacyKey],
                    kind: "expense",
                    sourceType: "expense",
                    sourceId: category.id,
                    label: category.name || "Expense",
                    amountCents,
                    date: new Date(scheduledDate),
                    dateKey: scheduledDateKey,
                    originalDate: new Date(sourceDate),
                    originalDateKey: sourceDateKey,
                    frequency: category.frequency,
                    paymentMethod: category.paymentMethod || "debit",
                    debtCategory: category.debtCategory || "none"
                });
            });
        });

        occurrences.sort((left, right) => {
            const dateDiff = left.date.getTime() - right.date.getTime();
            if (dateDiff !== 0) return dateDiff;
            return left.label.localeCompare(right.label);
        });
        return occurrences;
    };

    const buildDebtAdjustmentOccurrences = (state, monthKey) => {
        return (state.debt.adjustments || [])
            .filter((adjustment) => formatMonthKey(adjustment.date) === monthKey)
            .map((adjustment) => {
                const date = parseDate(adjustment.date) || parseMonthKey(monthKey) || todayDate();
                return {
                    key: buildOccurrenceKey("debtAdjustment", adjustment.id, adjustment.date),
                    kind: "debtAdjustment",
                    sourceType: "debtAdjustment",
                    sourceId: adjustment.id,
                    label: adjustment.note || "Debt adjustment",
                    amountCents: adjustment.amountCents,
                    date,
                    dateKey: formatDateKey(date),
                    frequency: "manual",
                    paymentMethod: "adjustment"
                };
            })
            .sort((left, right) => {
                const dateDiff = left.date.getTime() - right.date.getTime();
                if (dateDiff !== 0) return dateDiff;
                return left.label.localeCompare(right.label);
            });
    };

    const parseHistoryDateKey = (key, record) => {
        if (record?.dateKey && /^\d{4}-\d{2}-\d{2}$/.test(record.dateKey)) return record.dateKey;
        const parts = String(key || "").split(":");
        const candidate = parts.find((part) => /^\d{4}-\d{2}-\d{2}$/.test(part));
        return candidate || "";
    };

    const mergeHistoryOccurrences = (generated, historyMap, monthKey, fallbackKind) => {
        const generatedMap = new Map(generated.map((eventItem) => [eventItem.key, eventItem]));
        const aliasKeyMap = new Map();
        const orphans = [];

        generated.forEach((eventItem) => {
            (Array.isArray(eventItem.legacyKeys) ? eventItem.legacyKeys : []).forEach((legacyKey) => {
                if (!legacyKey || generatedMap.has(legacyKey) || aliasKeyMap.has(legacyKey)) return;
                aliasKeyMap.set(legacyKey, eventItem.key);
            });
        });

        Object.entries(historyMap || {}).forEach(([key, record]) => {
            const dateKey = parseHistoryDateKey(key, record);
            if (!dateKey || formatMonthKey(dateKey) !== monthKey) return;
            const matchedKey = generatedMap.has(key)
                ? key
                : aliasKeyMap.get(key);

            if (matchedKey && generatedMap.has(matchedKey)) {
                const existing = generatedMap.get(matchedKey);
                existing.history = {
                    status: record.status || "pending",
                    actualAmountCents: record.actualAmountCents,
                    completedAt: record.completedAt,
                    note: record.note
                };
                existing.snapshot = {
                    label: record.label || existing.label,
                    paymentMethod: record.paymentMethod || existing.paymentMethod,
                    frequency: record.frequency || existing.frequency
                };
                return;
            }

            const date = parseDate(dateKey);
            if (!date) return;
            orphans.push({
                key,
                kind: fallbackKind,
                sourceType: record.sourceType || fallbackKind,
                sourceId: record.sourceId || key,
                label: record.label || (fallbackKind === "income" ? "Income" : "Expense"),
                amountCents: clampCurrencyFloor(record.actualAmountCents),
                date,
                dateKey,
                frequency: record.frequency || "manual",
                paymentMethod: record.paymentMethod || (fallbackKind === "income" ? "deposit" : "debit"),
                debtCategory: fallbackKind === "expense" ? "none" : undefined,
                history: {
                    status: record.status || "completed",
                    actualAmountCents: clampCurrencyFloor(record.actualAmountCents),
                    completedAt: record.completedAt,
                    note: record.note
                },
                snapshot: {
                    label: record.label || "",
                    paymentMethod: record.paymentMethod || "",
                    frequency: record.frequency || "manual"
                },
                orphanHistory: true
            });
        });

        return generated.concat(orphans).sort(sortProjectedEvents);
    };

    const resolveEventStatus = (eventItem, today) => {
        if (eventItem.kind === "debtAdjustment") return "actual";
        if (eventItem.history?.status === "completed") return "actual";
        if (eventItem.date < today) return "needs-review";
        if (formatMonthKey(eventItem.date) === formatMonthKey(today)) return "current";
        return "projected";
    };

    const getMonthTemporalStatus = (monthContext, today) => {
        const currentMonthKey = formatMonthKey(today);
        if (monthContext.monthKey === currentMonthKey) return "current";
        return compareMonthKeys(monthContext.monthKey, currentMonthKey) < 0 ? "historical" : "future";
    };

    const buildMobileWeekSnapshot = (projection, asOfValue = new Date()) => {
        if (!projection || typeof projection !== "object") return null;

        const asOfDate = todayDate(asOfValue);
        const currentMonthKey = formatMonthKey(asOfDate);
        const month = Array.isArray(projection.months)
            ? projection.months.find((candidate) => candidate?.monthKey === currentMonthKey)
            : null;

        if (!month || !Array.isArray(month.weeks)) return null;

        const week = month.weeks.find((candidate) => {
            const startDate = candidate?.startDate instanceof Date
                ? candidate.startDate
                : parseDate(candidate?.startDate || candidate?.startDateKey);
            const endDate = candidate?.endDate instanceof Date
                ? candidate.endDate
                : parseDate(candidate?.endDate || candidate?.endDateKey);

            if (!startDate || !endDate) return false;

            startDate.setHours(0, 0, 0, 0);
            endDate.setHours(23, 59, 59, 999);

            return asOfDate >= startDate && asOfDate <= endDate;
        }) || null;

        if (!week) return null;

        const normalizeEvent = (eventItem) => ({
            key: String(eventItem?.key || ""),
            kind: String(eventItem?.kind || ""),
            label: String(eventItem?.label || ""),
            dateKey: String(
                eventItem?.dateKey ||
                formatDateKey(eventItem?.date) ||
                ""
            ),
            status: String(eventItem?.status || "projected"),
            amountCents: Math.round(Number(eventItem?.amountCents) || 0),
            impactCashCents: Math.round(Number(eventItem?.impactCashCents) || 0),
            cashAfterCents: Math.round(Number(eventItem?.cashAfterCents) || 0),
            debtAfterCents: Math.round(Number(eventItem?.debtAfterCents) || 0),
            paymentMethod: String(eventItem?.paymentMethod || ""),
            debtCategory: String(eventItem?.debtCategory || "")
        });

        return {
            schemaVersion: 1,
            generatedUtc: new Date().toISOString(),
            sourceStateVersion: Math.round(
                Number(projection.stateVersion) || CURRENT_VERSION
            ),
            monthKey: String(month.monthKey || currentMonthKey),
            monthLabel: String(month.label || ""),
            weekId: String(week.id || ""),
            weekLabel: String(week.label || ""),
            startDate: formatDateKey(week.startDate),
            endDate: formatDateKey(week.endDate),
            status: String(week.status || "current"),
            openingCashCents: Math.round(Number(week.openingCashCents) || 0),
            incomeCents: Math.round(Number(week.incomeCents) || 0),
            debitBillsCents: Math.round(Number(week.debitBillsCents) || 0),
            creditBillsCents: Math.round(Number(week.creditBillsCents) || 0),
            requiredExpensesCents: Math.round(
                Number(week.requiredExpensesCents) || 0
            ),
            requiredDebtMinimumCents: Math.round(
                Number(week.requiredDebtMinimumCents) || 0
            ),
            extraDebtPaymentCents: Math.round(
                Number(week.extraDebtPaymentCents) || 0
            ),
            closingCashCents: Math.round(Number(week.closingCashCents) || 0),
            openingDebtCents: Math.round(Number(week.openingDebtCents) || 0),
            closingDebtCents: Math.round(Number(week.closingDebtCents) || 0),
            events: Array.isArray(week.events)
                ? week.events.map(normalizeEvent)
                : []
        };
    };

    const buildMobileMonthSnapshot = (projection, asOfValue = new Date()) => {
        if (!projection || typeof projection !== "object") return null;

        const asOfDate = todayDate(asOfValue);
        const currentMonthKey = formatMonthKey(asOfDate);
        const month = Array.isArray(projection.months)
            ? projection.months.find(
                (candidate) => candidate?.monthKey === currentMonthKey
            )
            : null;

        if (!month || !Array.isArray(month.weeks)) return null;

        const monthContext = getMonthContext(
            String(month.monthKey || currentMonthKey)
        );

        const normalizeWeek = (week) => {
            const debitBillsCents = Math.round(
                Number(week?.debitBillsCents) || 0
            );
            const creditBillsCents = Math.round(
                Number(week?.creditBillsCents) || 0
            );
            const extraDebtPaymentCents = Math.round(
                Number(week?.extraDebtPaymentCents) || 0
            );

            return {
                weekId: String(week?.id || ""),
                weekLabel: String(week?.label || ""),
                startDate: formatDateKey(week?.startDate),
                endDate: formatDateKey(week?.endDate),
                status: String(week?.status || "current"),
                incomeCents: Math.round(
                    Number(week?.incomeCents) || 0
                ),
                debitBillsCents,
                creditBillsCents,
                requiredDebtMinimumCents: Math.round(
                    Number(week?.requiredDebtMinimumCents) || 0
                ),
                extraDebtPaymentCents,
                outflowCents:
                    debitBillsCents +
                    creditBillsCents +
                    extraDebtPaymentCents,
                closingCashCents: Math.round(
                    Number(week?.closingCashCents) || 0
                ),
                closingDebtCents: Math.round(
                    Number(week?.closingDebtCents) || 0
                )
            };
        };

        const normalizedWeeks = month.weeks.map(normalizeWeek);

        const allEvents = month.weeks.flatMap((week) =>
            Array.isArray(week?.events) ? week.events : []
        );

        const obligationEvents = allEvents
            .filter((eventItem) =>
                Math.round(Number(eventItem?.amountCents) || 0) > 0 &&
                Math.round(Number(eventItem?.impactCashCents) || 0) < 0
            )
            .sort((left, right) =>
                Math.round(Number(right?.amountCents) || 0) -
                Math.round(Number(left?.amountCents) || 0)
            );

        const largestEvent = obligationEvents[0] || null;

        const warnings = Array.isArray(month.warnings)
            ? month.warnings
                .map((warning) => String(warning || "").trim())
                .filter(Boolean)
            : [];

        return {
            schemaVersion: 1,
            generatedUtc: new Date().toISOString(),
            sourceStateVersion: Math.round(
                Number(projection.stateVersion) || CURRENT_VERSION
            ),
            monthKey: String(month.monthKey || currentMonthKey),
            monthLabel: String(month.label || ""),
            startDate: formatDateKey(monthContext.startDate),
            endDate: formatDateKey(monthContext.endDate),
            temporalStatus: String(
                month.temporalStatus ||
                getMonthTemporalStatus(monthContext, asOfDate)
            ),
            status: String(month.status || "current"),
            pressureSummary:
                warnings.length > 0
                    ? warnings.join(" ")
                    : null,
            openingCashCents: Math.round(
                Number(month.openingCashCents) || 0
            ),
            incomeCents: Math.round(
                Number(month.scheduledIncomeCents) || 0
            ),
            debitBillsCents: normalizedWeeks.reduce(
                (total, week) => total + week.debitBillsCents,
                0
            ),
            creditBillsCents: normalizedWeeks.reduce(
                (total, week) => total + week.creditBillsCents,
                0
            ),
            requiredExpensesCents: Math.round(
                Number(month.requiredExpensesCents) || 0
            ),
            requiredDebtMinimumCents: Math.round(
                Number(month.requiredDebtMinimumCents) || 0
            ),
            extraDebtPaymentCents: Math.round(
                Number(month.extraDebtPaymentsCents) || 0
            ),
            endingCashCents: Math.round(
                Number(month.endingCashCents) || 0
            ),
            openingDebtCents: Math.round(
                Number(month.openingDebtCents) || 0
            ),
            endingDebtCents: Math.round(
                Number(month.endingDebtCents) || 0
            ),
            savingsContributionCents: null,
            savingsProjectionStatus: "not-projected-by-expense-lens",
            largestObligation: largestEvent
                ? {
                    key: String(largestEvent.key || ""),
                    title: String(largestEvent.label || ""),
                    dateKey: String(
                        largestEvent.dateKey ||
                        formatDateKey(largestEvent.date) ||
                        ""
                    ),
                    amountCents: Math.round(
                        Number(largestEvent.amountCents) || 0
                    ),
                    kind: String(largestEvent.kind || "")
                }
                : null,
            weeks: normalizedWeeks
        };
    };

    /**
     * Persists the canonical Expense Lens output by calendar period so native
     * clients can resolve the real current week/month at request time. The
     * server only selects from these web-authored snapshots; it never repeats
     * Expense Lens arithmetic or substitutes a UI-selected month.
     */
    const buildMobilePeriodProjection = (projection, generatedValue = new Date()) => {
        if (!projection || typeof projection !== "object" || !Array.isArray(projection.months)) {
            return null;
        }

        const generatedDate = generatedValue instanceof Date
            ? new Date(generatedValue)
            : new Date();
        const generatedUtc = Number.isNaN(generatedDate.getTime())
            ? new Date().toISOString()
            : generatedDate.toISOString();
        const currentMonthKey = formatMonthKey(generatedDate);

        const periods = projection.months
            .filter((month) =>
                compareMonthKeys(month?.monthKey, currentMonthKey) >= 0
            )
            .slice(0, MAX_MOBILE_PERIOD_MONTHS)
            .map((month) => {
                const monthKey = String(month?.monthKey || "");
                const monthDate = parseMonthKey(monthKey);
                if (!monthDate || !Array.isArray(month?.weeks)) return null;

                const monthSnapshot = buildMobileMonthSnapshot(
                    projection,
                    monthDate
                );
                if (!monthSnapshot) return null;

                const weekSnapshots = month.weeks
                    .map((week) => {
                        const weekDate = week?.startDate instanceof Date
                            ? week.startDate
                            : parseDate(week?.startDate || week?.startDateKey);
                        return weekDate
                            ? buildMobileWeekSnapshot(projection, weekDate)
                            : null;
                    })
                    .filter(Boolean);

                return {
                    monthKey,
                    monthSnapshot,
                    weekSnapshots
                };
            })
            .filter(Boolean);

        if (periods.length === 0) return null;

        return {
            schemaVersion: 1,
            generatedUtc,
            sourceStateVersion: Math.round(
                Number(projection.stateVersion) || CURRENT_VERSION
            ),
            projectionStartMonthKey: String(
                periods[0].monthKey
            ),
            projectionEndMonthKey: String(
                periods[periods.length - 1].monthKey
            ),
            periods
        };
    };

    const projectExpenseLensTimeline = (input = {}) => {
        const state = normalizeState(input.state || {});
        const today = todayDate(input.asOfDate || new Date());
        const selectedMonthKey = formatMonthKey(input.selectedMonthKey || today);
        const anchorMonthKey = formatMonthKey(state.debt.asOfDate || today);
        const overrideMonthKeys = Object.keys(state.monthlyStartingBalanceOverrides || {}).filter(Boolean);
        const historyMonthKeys = [
            ...Object.values(state.occurrenceHistory.incomes || {}).map((record) => formatMonthKey(record.dateKey)),
            ...Object.values(state.occurrenceHistory.expenses || {}).map((record) => formatMonthKey(record.dateKey))
        ].filter(Boolean);

        const startCandidates = [selectedMonthKey, anchorMonthKey, ...overrideMonthKeys, ...historyMonthKeys].filter(Boolean);
        let projectionStartMonthKey = startCandidates.sort(compareMonthKeys)[0] || selectedMonthKey;
        if (compareMonthKeys(projectionStartMonthKey, selectedMonthKey) > 0) {
            projectionStartMonthKey = selectedMonthKey;
        }

        const months = [];
        const monthMap = new Map();
        const protectedCashReserveCents = clampCurrencyFloor(state.projectionSettings?.protectedCashReserveCents || 0);
        const selectedMonthDistance = Math.max(0, Math.round((parseMonthKey(selectedMonthKey) - parseMonthKey(projectionStartMonthKey)) / (1000 * 60 * 60 * 24 * 30)));
        const minimumProjectionMonths = Math.max(18, selectedMonthDistance + 18);
        const maxProjectionMonths = Math.min(MAX_PROJECTION_MONTHS, Math.max(minimumProjectionMonths, input.horizonMonths || 36));

        let carryCashCents = 0;
        let carryDebtCents = clampCurrencyFloor(state.debt.openingBalanceCents);
        let payoffDate = null;
        let firstDebtFreeMonth = null;
        let firstPositiveMonthAfterPayoff = null;
        let maxDebtBalanceCents = carryDebtCents;
        let maxCashDeficitCents = 0;
        let payoffGraceMonths = 0;
        const debtPaymentEvents = [];

        for (let monthIndex = 0; monthIndex < maxProjectionMonths; monthIndex += 1) {
            const monthKey = addMonths(projectionStartMonthKey, monthIndex);
            const monthContext = getMonthContext(monthKey);
            const override = state.monthlyStartingBalanceOverrides?.[monthKey] || null;
            const openingCashCents = override ? Math.round(override.amountCents || 0) : carryCashCents;
            const openingDebtCents = carryDebtCents;

            const generatedIncome = buildScheduledIncomeOccurrences(state, monthKey);
            const generatedExpenses = buildScheduledExpenseOccurrences(state, monthKey);
            const generatedAdjustments = buildDebtAdjustmentOccurrences(state, monthKey);
            const incomes = mergeHistoryOccurrences(generatedIncome, state.occurrenceHistory.incomes, monthKey, "income");
            const expenses = mergeHistoryOccurrences(generatedExpenses, state.occurrenceHistory.expenses, monthKey, "expense");
            const adjustments = generatedAdjustments;
            const eventMapByWeek = new Map();
            const weeks = getCalendarWeeksForMonth(monthKey);
            weeks.forEach((week) => eventMapByWeek.set(week.id, []));

            incomes.concat(expenses).concat(adjustments).forEach((eventItem) => {
                const week = weeks.find((candidate) => eventItem.date >= candidate.startDate && eventItem.date <= candidate.endDate);
                if (week) eventMapByWeek.get(week.id).push(eventItem);
            });

            let runningCashCents = openingCashCents;
            let runningDebtCents = openingDebtCents;
            let scheduledIncomeCents = 0;
            let requiredExpensesCents = 0;
            let requiredDebtMinimumCents = 0;
            let extraDebtPaymentsCents = 0;
            let hasHistoricalGaps = false;
            const weekRows = [];

            weeks.forEach((week) => {
                const weekEvents = (eventMapByWeek.get(week.id) || []).slice().sort(sortProjectedEvents);

                const weekOpeningCashCents = runningCashCents;
                const weekOpeningDebtCents = runningDebtCents;
                let weekIncomeCents = 0;
                let weekDebitBillsCents = 0;
                let weekCreditBillsCents = 0;
                let weekRequiredExpenseCents = 0;
                let weekRequiredDebtMinimumCents = 0;
                let extraDebtPaymentCents = 0;
                const renderedEvents = [];

                weekEvents.forEach((eventItem) => {
                    const status = resolveEventStatus(eventItem, today);
                    if (status === "needs-review") hasHistoricalGaps = true;

                    if (eventItem.kind === "income") {
                        const appliedCents = eventItem.history?.status === "completed" && Number.isFinite(eventItem.history.actualAmountCents)
                            ? Math.max(0, Math.round(eventItem.history.actualAmountCents))
                            : eventItem.amountCents;
                        runningCashCents += appliedCents;
                        weekIncomeCents += appliedCents;
                        scheduledIncomeCents += appliedCents;
                        renderedEvents.push({
                            ...eventItem,
                            status,
                            amountCents: appliedCents,
                            impactCashCents: appliedCents,
                            cashAfterCents: runningCashCents,
                            debtAfterCents: runningDebtCents
                        });
                        return;
                    }

                    if (eventItem.kind === "expense") {
                        const scheduledCents = eventItem.history?.status === "completed" && Number.isFinite(eventItem.history.actualAmountCents)
                            ? Math.max(0, Math.round(eventItem.history.actualAmountCents))
                            : eventItem.amountCents;
                        const tracksDebt = isTrackedDebtMinimumCategory(eventItem.debtCategory);
                        const isCreditBill = !tracksDebt && isCreditPaymentMethod(eventItem.paymentMethod);
                        const appliedCashCents = tracksDebt && runningDebtCents <= 0 && !eventItem.history?.status
                            ? 0
                            : scheduledCents;
                        runningCashCents -= appliedCashCents;
                        requiredExpensesCents += appliedCashCents;
                        weekRequiredExpenseCents += appliedCashCents;

                        if (!tracksDebt) {
                            if (isCreditBill) {
                                weekCreditBillsCents += appliedCashCents;
                            } else {
                                weekDebitBillsCents += appliedCashCents;
                            }
                        }

                        let appliedDebtCents = 0;
                        if (tracksDebt && appliedCashCents > 0 && runningDebtCents > 0) {
                            appliedDebtCents = Math.min(runningDebtCents, appliedCashCents);
                            runningDebtCents = clampCurrencyFloor(runningDebtCents - appliedDebtCents);
                            requiredDebtMinimumCents += appliedDebtCents;
                            weekRequiredDebtMinimumCents += appliedDebtCents;
                            debtPaymentEvents.push({
                                key: buildOccurrenceKey("debtMinimum", eventItem.sourceId, eventItem.dateKey),
                                kind: "debtMinimum",
                                label: eventItem.label,
                                sourceId: eventItem.sourceId,
                                date: eventItem.date,
                                dateKey: eventItem.dateKey,
                                amountCents: appliedDebtCents,
                                resultingDebtBalanceCents: runningDebtCents,
                                monthKey,
                                weekId: week.id,
                                status: status === "actual" ? "actual" : "projected",
                                note: "Required minimum payment"
                            });
                            if (!payoffDate && runningDebtCents === 0) {
                                payoffDate = eventItem.dateKey;
                            }
                        }

                        renderedEvents.push({
                            ...eventItem,
                            status,
                            amountCents: scheduledCents,
                            impactCashCents: -appliedCashCents,
                            appliedCashCents,
                            appliedDebtCents,
                            cashAfterCents: runningCashCents,
                            debtAfterCents: runningDebtCents
                        });
                        return;
                    }

                    if (eventItem.kind === "debtAdjustment") {
                        runningDebtCents = clampCurrencyFloor(runningDebtCents + eventItem.amountCents);
                        renderedEvents.push({
                            ...eventItem,
                            status: "actual",
                            impactCashCents: 0,
                            cashAfterCents: runningCashCents,
                            debtAfterCents: runningDebtCents
                        });
                        maxDebtBalanceCents = Math.max(maxDebtBalanceCents, runningDebtCents);
                    }
                });

                maxDebtBalanceCents = Math.max(maxDebtBalanceCents, weekOpeningDebtCents, runningDebtCents);
                maxCashDeficitCents = Math.min(maxCashDeficitCents, runningCashCents);

                const weekStatus = week.endDate < today
                    ? (renderedEvents.every((eventItem) => eventItem.status === "actual") ? "actual" : "historical-unreconciled")
                    : (formatMonthKey(week.startDate) === formatMonthKey(today) ? "current" : "projected");

                weekRows.push({
                    ...week,
                    status: weekStatus,
                    openingCashCents: weekOpeningCashCents,
                    closingCashCents: runningCashCents,
                    openingDebtCents: weekOpeningDebtCents,
                    closingDebtCents: runningDebtCents,
                    incomeCents: weekIncomeCents,
                    debitBillsCents: weekDebitBillsCents,
                    creditBillsCents: weekCreditBillsCents,
                    requiredExpensesCents: weekRequiredExpenseCents,
                    requiredDebtMinimumCents: weekRequiredDebtMinimumCents,
                    extraDebtPaymentCents,
                    events: renderedEvents
                });
            });

            if (runningDebtCents > 0 && weekRows.length > 0) {
                const availableForExtraDebtCents = Math.max(0, runningCashCents - protectedCashReserveCents);
                const monthEndExtraDebtPaymentCents = Math.min(availableForExtraDebtCents, runningDebtCents);
                if (monthEndExtraDebtPaymentCents > 0) {
                    runningCashCents -= monthEndExtraDebtPaymentCents;
                    runningDebtCents = clampCurrencyFloor(runningDebtCents - monthEndExtraDebtPaymentCents);
                    extraDebtPaymentsCents += monthEndExtraDebtPaymentCents;

                    const targetWeek = weekRows[weekRows.length - 1];
                    const extraDate = formatDateKey(targetWeek.endDate) || targetWeek.id;
                    const extraDebtStatus = targetWeek.endDate < today && !hasHistoricalGaps ? "actual" : "projected";
                    const extraDebtEvent = {
                        key: buildOccurrenceKey("extraDebt", `week-${targetWeek.id}`, extraDate),
                        kind: "extraDebt",
                        sourceType: "debt",
                        sourceId: `week-${targetWeek.id}`,
                        label: "Extra debt payoff",
                        date: new Date(targetWeek.endDate),
                        dateKey: extraDate,
                        amountCents: monthEndExtraDebtPaymentCents,
                        impactCashCents: -monthEndExtraDebtPaymentCents,
                        cashAfterCents: runningCashCents,
                        debtAfterCents: runningDebtCents,
                        status: extraDebtStatus
                    };

                    targetWeek.extraDebtPaymentCents += monthEndExtraDebtPaymentCents;
                    targetWeek.closingCashCents = runningCashCents;
                    targetWeek.closingDebtCents = runningDebtCents;
                    targetWeek.events.push(extraDebtEvent);

                    debtPaymentEvents.push({
                        key: extraDebtEvent.key,
                        kind: "extraDebt",
                        label: extraDebtEvent.label,
                        sourceId: extraDebtEvent.sourceId,
                        date: extraDebtEvent.date,
                        dateKey: extraDebtEvent.dateKey,
                        amountCents: monthEndExtraDebtPaymentCents,
                        resultingDebtBalanceCents: runningDebtCents,
                        monthKey,
                        weekId: targetWeek.id,
                        status: extraDebtStatus,
                        note: "Remaining cash strategy"
                    });

                    if (!payoffDate && runningDebtCents === 0) {
                        payoffDate = extraDate;
                    }
                }
            }

            const monthStatus = (() => {
                const temporal = getMonthTemporalStatus(monthContext, today);
                if (temporal !== "historical") return temporal;
                return hasHistoricalGaps ? "historical-unreconciled" : "historical-reconciled";
            })();

            if (!firstDebtFreeMonth && openingDebtCents > 0 && runningDebtCents === 0) {
                firstDebtFreeMonth = monthKey;
            }

            if (payoffDate && !firstPositiveMonthAfterPayoff && runningCashCents > 0 && compareMonthKeys(monthKey, formatMonthKey(payoffDate)) >= 0) {
                firstPositiveMonthAfterPayoff = monthKey;
            }

            const monthRecord = {
                monthKey,
                label: new Intl.DateTimeFormat("en-US", { month: "long", year: "numeric" }).format(monthContext.startDate),
                temporalStatus: getMonthTemporalStatus(monthContext, today),
                status: monthStatus,
                startingBalanceSource: override
                    ? "manual-override"
                    : months.length === 0
                        ? "baseline"
                        : "rolling-balance",
                override: override ? { ...override } : null,
                openingCashCents,
                openingDebtCents,
                scheduledIncomeCents,
                requiredExpensesCents,
                requiredDebtMinimumCents,
                extraDebtPaymentsCents,
                endingCashCents: runningCashCents,
                endingDebtCents: runningDebtCents,
                creditPaymentDayOfMonth: state.projectionSettings?.creditPaymentDayOfMonth || null,
                isReconciled: !hasHistoricalGaps,
                warnings: [],
                weeks: weekRows
            };

            if (monthRecord.temporalStatus === "historical" && hasHistoricalGaps) {
                monthRecord.warnings.push("Historical month is not fully reconciled.");
            }
            if (monthRecord.temporalStatus === "historical" && compareMonthKeys(monthKey, anchorMonthKey) < 0) {
                monthRecord.warnings.push("Projection before the debt as-of date uses the opening debt balance as the baseline.");
            }

            months.push(monthRecord);
            monthMap.set(monthKey, monthRecord);
            carryCashCents = runningCashCents;
            carryDebtCents = runningDebtCents;

            if (runningDebtCents === 0 && payoffDate) {
                payoffGraceMonths += 1;
            }

            if (compareMonthKeys(monthKey, selectedMonthKey) >= 0 && runningDebtCents === 0 && payoffGraceMonths >= 12) {
                break;
            }
        }

        const selectedMonth = monthMap.get(selectedMonthKey) || months.find((month) => month.monthKey === selectedMonthKey) || months[0] || null;
        const debtPaidWithinHorizon = carryDebtCents === 0;

        return {
            stateVersion: CURRENT_VERSION,
            selectedMonthKey,
            projectionStartMonthKey,
            projectionEndMonthKey: months[months.length - 1]?.monthKey || selectedMonthKey,
            currentMonthKey: formatMonthKey(today),
            months,
            selectedMonth,
            debt: {
                openingBalanceCents: state.debt.openingBalanceCents,
                currentBalanceCents: selectedMonth?.endingDebtCents ?? carryDebtCents,
                projectedPayoffDate: payoffDate,
                projectedInterestExcluded: state.debt.projectedInterestExcluded !== false,
                monthlyMinimumPaymentsCents: state.debt.monthlyMinimumPaymentsCents,
                paymentHistory: debtPaymentEvents
            },
            summary: {
                debtPayoffDate: payoffDate,
                debtFreeMonth: firstDebtFreeMonth,
                firstPositiveMonthAfterDebtPayoff: firstPositiveMonthAfterPayoff,
                maximumProjectedCashDeficitCents: Math.abs(Math.min(0, maxCashDeficitCents)),
                maximumProjectedDebtBalanceCents: maxDebtBalanceCents,
                unpayableWithinHorizon: !debtPaidWithinHorizon
            }
        };
    };

    return {
        CURRENT_VERSION,
        MAX_PROJECTION_MONTHS,
        MAX_MOBILE_PERIOD_MONTHS,
        normalizeFrequency,
        parseMoneyToCents,
        centsToDollars,
        parseDate,
        formatDateKey,
        formatMonthKey,
        parseMonthKey,
        compareMonthKeys,
        addMonths,
        getDefaultAnchorDate,
        getScheduledOccurrenceDays,
        getCalendarWeeksForMonth,
        buildOccurrenceKey,
        normalizeState,
        summarizeIncomeGroups,
        summarizeExpenseCategories,
        buildMobileWeekSnapshot,
        buildMobileMonthSnapshot,
        buildMobilePeriodProjection,
        projectExpenseLensTimeline
    };
});

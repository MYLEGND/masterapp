import SwiftUI

struct JourneyMomentsRail: View {
    let moments: [LegendJourneyMoment]
    let onSelect: (LegendJourneyMoment) -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            LazyHStack(
                alignment: .top,
                spacing: LegendSpacing.sm
            ) {
                ForEach(moments) { moment in
                    momentButton(moment)
                }
            }
            .padding(.vertical, LegendSpacing.xxs)
        }
        .contentMargins(.horizontal, 1, for: .scrollContent)
        .accessibilityElement(children: .contain)
    }

    private func momentButton(
        _ moment: LegendJourneyMoment
    ) -> some View {
        Button {
            UISelectionFeedbackGenerator().selectionChanged()
            onSelect(moment)
        } label: {
            VStack(spacing: LegendSpacing.xs) {
                momentIcon(moment)

                VStack(spacing: LegendSpacing.xxs) {
                    Text(moment.title)
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(LegendPalette.label)
                        .multilineTextAlignment(.center)
                        .lineLimit(2)

                    Text(moment.subtitle)
                        .font(.caption2)
                        .foregroundStyle(
                            LegendPalette.secondaryLabel
                        )
                        .multilineTextAlignment(.center)
                        .lineLimit(2)
                }
                .frame(width: 92)
            }
            .frame(width: 102, alignment: .top)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(moment.title)
        .accessibilityHint(moment.subtitle)
        .accessibilityAddTraits(.isButton)
    }

    private func momentIcon(
        _ moment: LegendJourneyMoment
    ) -> some View {
        ZStack {
            Circle()
                .fill(
                    moment.isCurrentUser
                        ? LegendPalette.primaryNavy
                        : LegendPalette.elevatedSurface
                )
                .frame(width: 68, height: 68)

            Circle()
                .stroke(
                    moment.isCurrentUser
                        ? LegendPalette.gold
                        : LegendPalette.gold.opacity(0.48),
                    lineWidth: moment.isCurrentUser ? 3 : 2
                )
                .frame(width: 68, height: 68)

            Circle()
                .stroke(
                    moment.isCurrentUser
                        ? Color.white.opacity(0.2)
                        : LegendPalette.separator.opacity(0.25),
                    lineWidth: 1
                )
                .frame(width: 58, height: 58)

            Image(systemName: moment.kind.systemImageName)
                .font(.system(size: 23, weight: .semibold))
                .foregroundStyle(
                    moment.isCurrentUser
                        ? Color.white
                        : LegendPalette.primaryNavy
                )
        }
        .overlay(alignment: .bottomTrailing) {
            if moment.isCurrentUser {
                Image(systemName: "plus")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 23, height: 23)
                    .background(
                        LegendPalette.gold,
                        in: Circle()
                    )
                    .overlay {
                        Circle()
                            .stroke(
                                LegendPalette.elevatedSurface,
                                lineWidth: 2
                            )
                    }
            }
        }
        .accessibilityHidden(true)
    }
}

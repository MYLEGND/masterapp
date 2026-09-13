import SwiftUI

/// Renders the server-authoritative passage as reading prose. The raw passage
/// remains untouched; only confidently recognized line-leading verse markers
/// receive typographic treatment.
struct LegendDailyScripturePassageView: View {
    let scripture: MobileDailyScripture

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
            ForEach(DailyScripturePassageFormatter.paragraphs(for: scripture)) { paragraph in
                prose(paragraph)
                    .font(LegendNextTypography.body)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .lineSpacing(6)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(paragraph.accessibilityText)
            }
        }
    }

    private func prose(_ paragraph: DailyScripturePassageParagraph) -> Text {
        var rendered = Text("")
        for (index, segment) in paragraph.segments.enumerated() {
            if index > 0 {
                rendered = rendered + Text(" ")
            }

            if let number = segment.number {
                rendered = rendered + Text("\(number)")
                    .font(.system(size: 11, weight: .semibold, design: .serif))
                    .foregroundColor(LegendNextColor.gold)
                    .baselineOffset(5)
                rendered = rendered + Text(" ")
            }

            rendered = rendered + Text(segment.text)
        }
        return rendered
    }
}

struct DailyScripturePassageParagraph: Identifiable, Equatable {
    let id: String
    let segments: [DailyScripturePassageSegment]

    var accessibilityText: String {
        segments.map { segment in
            guard let number = segment.number else { return segment.text }
            return LegendLocalized(
                "Verse {number}. {text}",
                context: "accessibility copy",
                arguments: ["number": number, "text": segment.text])
        }
        .joined(separator: " ")
    }
}

struct DailyScripturePassageSegment: Equatable {
    let number: Int?
    let text: String
}

enum DailyScripturePassageFormatter {
    private static let numberedLine = try! NSRegularExpression(
        pattern: "^\\s*([1-9][0-9]{0,2})[.)]?\\s+(.+?)\\s*$")

    static func paragraphs(for scripture: MobileDailyScripture) -> [DailyScripturePassageParagraph] {
        if !scripture.verses.isEmpty {
            return [DailyScripturePassageParagraph(
                id: "catalog",
                segments: scripture.verses.enumerated().map { index, verse in
                    DailyScripturePassageSegment(number: index + 1, text: verse)
                })]
        }

        let rawParagraphs = scripture.passageText
            .components(separatedBy: "\n\n")
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let sourceParagraphs = rawParagraphs.isEmpty ? [scripture.text] : rawParagraphs

        return sourceParagraphs.enumerated().map { index, rawParagraph in
            let lines = rawParagraph
                .components(separatedBy: .newlines)
                .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            let segments = lines.compactMap(parseNumberedLine)
            if !lines.isEmpty && segments.count == lines.count {
                return DailyScripturePassageParagraph(
                    id: "numbered-\(index)",
                    segments: segments)
            }

            return DailyScripturePassageParagraph(
                id: "raw-\(index)",
                segments: [DailyScripturePassageSegment(number: nil, text: rawParagraph)])
        }
    }

    private static func parseNumberedLine(_ line: String) -> DailyScripturePassageSegment? {
        let range = NSRange(line.startIndex..., in: line)
        guard let match = numberedLine.firstMatch(in: line, range: range),
              let numberRange = Range(match.range(at: 1), in: line),
              let textRange = Range(match.range(at: 2), in: line),
              let number = Int(line[numberRange]) else {
            return nil
        }

        return DailyScripturePassageSegment(number: number, text: String(line[textRange]))
    }
}

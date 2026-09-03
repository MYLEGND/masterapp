#!/usr/bin/env ruby
# frozen_string_literal: true

require "digest"
require "json"
require "pathname"

ROOT = Pathname.new(__dir__).parent
SWIFT_ROOT = ROOT.join("Legend-ios/Legend")
KOTLIN_ROOT = ROOT.join("Legend-Android/app/src/main/java")
MANIFEST = ROOT.join("Legend-Design/legend-application-copy.json")
DESIGN = ROOT.join("Legend-Design/legend-design.tokens.json")
VISUAL = "visual interface copy"
ACCESSIBILITY = "accessibility copy"
LITERAL = /"(?:\\.|[^"\\])*"/

def literal_value(token)
  JSON.parse(token)
rescue JSON::ParserError
  nil
end

def eligible?(value, platform)
  return false if value.nil? || value.strip.empty?
  return false unless value.match?(/[[:alpha:]]/)
  return false if platform == :swift && value.include?("\\(")
  return false if platform == :kotlin && value.include?("$")

  true
end

def rewrite_swift(source)
  visual_apis = %w[Text Button Label TextField SecureField ProgressView Menu Picker Toggle Section]
  visual_apis.each do |api|
    source.gsub!(/(\b#{api}\s*\(\s*)(#{LITERAL})/) do
      prefix = Regexp.last_match(1)
      token = Regexp.last_match(2)
      value = literal_value(token)
      eligible?(value, :swift) ? "#{prefix}LegendLocalized(#{token})" : Regexp.last_match(0)
    end
  end
  %w[navigationTitle alert confirmationDialog].each do |api|
    source.gsub!(/(\.#{api}\s*\(\s*)(#{LITERAL})/) do
      prefix = Regexp.last_match(1)
      token = Regexp.last_match(2)
      value = literal_value(token)
      eligible?(value, :swift) ? "#{prefix}LegendLocalized(#{token})" : Regexp.last_match(0)
    end
  end
  %w[accessibilityLabel accessibilityHint].each do |api|
    source.gsub!(/(\.#{api}\s*\(\s*)(#{LITERAL})/) do
      prefix = Regexp.last_match(1)
      token = Regexp.last_match(2)
      value = literal_value(token)
      eligible?(value, :swift) ? "#{prefix}LegendLocalized(#{token}, context: \"#{ACCESSIBILITY}\")" : Regexp.last_match(0)
    end
  end
  # App-owned presentation models and custom components often receive copy as
  # named String arguments before eventually rendering it. Localize those
  # source literals at their declaration point so generic views never need to
  # guess whether an arbitrary runtime String is application copy or user data.
  %w[title detail message retryTitle eyebrow placeholder emptyTitle emptyMessage].each do |argument|
    source.gsub!(/(\b#{argument}\s*:\s*)(#{LITERAL})/) do
      prefix = Regexp.last_match(1)
      token = Regexp.last_match(2)
      value = literal_value(token)
      eligible?(value, :swift) ? "#{prefix}LegendLocalized(#{token})" : Regexp.last_match(0)
    end
  end
  %w[accessibilityLabel accessibilityHint].each do |argument|
    source.gsub!(/(\b#{argument}\s*:\s*)(#{LITERAL})/) do
      prefix = Regexp.last_match(1)
      token = Regexp.last_match(2)
      value = literal_value(token)
      eligible?(value, :swift) ? "#{prefix}LegendLocalized(#{token}, context: \"#{ACCESSIBILITY}\")" : Regexp.last_match(0)
    end
  end
  source
end

def interpolation_end(source, opening_parenthesis)
  depth = 1
  quote = nil
  escaped = false
  index = opening_parenthesis + 1
  while index < source.length
    character = source[index]
    if quote
      if escaped
        escaped = false
      elsif character == "\\"
        escaped = true
      elsif character == quote
        quote = nil
      end
    elsif character == '"' || character == "'"
      quote = character
    elsif character == "("
      depth += 1
    elsif character == ")"
      depth -= 1
      return index if depth.zero?
    end
    index += 1
  end
  nil
end

def rewrite_dynamic_swift(source)
  marker = 'LegendLocalized("'
  cursor = 0
  while (start = source.index(marker, cursor))
    content_start = start + marker.length
    index = content_start
    template = +""
    expressions = []
    closing_quote = nil
    while index < source.length
      if source[index, 2] == "\\("
        finish = interpolation_end(source, index + 1)
        break unless finish
        expressions << source[(index + 2)...finish]
        template << "{value#{expressions.length}}"
        index = finish + 1
      elsif source[index] == '"'
        closing_quote = index
        break
      else
        template << source[index]
        index += 1
      end
    end
    unless closing_quote && !expressions.empty?
      cursor = content_start
      next
    end

    suffix = source[closing_quote..]
    call_end = suffix.match(/\A"\s*(?:,\s*context:\s*("(?:\\.|[^"\\])*")\s*)?\)/m)
    unless call_end
      cursor = closing_quote + 1
      next
    end
    context_token = call_end[1]
    original_literal = source[(content_start - 1)..closing_quote]
    static_text = template.gsub(/\{value\d+\}/, "").gsub(/[^[:alpha:]]/, "")
    replacement = if static_text.empty?
                    original_literal
                  else
                    arguments = expressions.each_with_index.map do |expression, position|
                      "\"value#{position + 1}\": String(describing: (#{expression}))"
                    end.join(", ")
                    context = context_token ? ", context: #{context_token}" : ""
                    "LegendLocalized(#{JSON.generate(template)}#{context}, arguments: [#{arguments}])"
                  end
    finish = closing_quote + call_end[0].length
    source[start...finish] = replacement
    cursor = start + replacement.length
  end
  source
end

def rewrite_kotlin(source)
  source.gsub!(/(\bText\s*\(\s*)(#{LITERAL})/) do
    prefix = Regexp.last_match(1)
    token = Regexp.last_match(2)
    value = literal_value(token)
    eligible?(value, :kotlin) ? "#{prefix}legendLocalized(#{token})" : Regexp.last_match(0)
  end
  source.gsub!(/(\bcontentDescription\s*=\s*)(#{LITERAL})/) do
    prefix = Regexp.last_match(1)
    token = Regexp.last_match(2)
    value = literal_value(token)
    eligible?(value, :kotlin) ? "#{prefix}legendLocalized(#{token}, \"#{ACCESSIBILITY}\")" : Regexp.last_match(0)
  end
  # Compose accepts content descriptions positionally for Icon/Image. Keep
  # those accessibility strings on the same canonical path as named
  # contentDescription arguments.
  source.gsub!(/(\b(?:Icon|Image)\s*\([^\n,]+,\s*)(#{LITERAL})/) do
    prefix = Regexp.last_match(1)
    token = Regexp.last_match(2)
    value = literal_value(token)
    eligible?(value, :kotlin) ? "#{prefix}legendLocalized(#{token}, \"#{ACCESSIBILITY}\")" : Regexp.last_match(0)
  end
  source
end

if ARGV.include?("--rewrite")
  Dir.glob(SWIFT_ROOT.join("**/*.swift")).sort.each do |path|
    original = File.read(path)
    rewritten = rewrite_dynamic_swift(rewrite_swift(original.dup))
    File.write(path, rewritten) unless rewritten == original
  end
  Dir.glob(KOTLIN_ROOT.join("**/*.kt")).sort.each do |path|
    original = File.read(path)
    rewritten = rewrite_kotlin(original.dup)
    File.write(path, rewritten) unless rewritten == original
  end
end

entries = {}
add = lambda do |source, context|
  return unless eligible?(source, :swift)

  identity = Digest::SHA256.hexdigest("#{source}\n#{context}")
  policy = if ["LEGEND", "LEGEND®", "Legend", "Legend AI", "Legend® Ai", "OpenAI"].include?(source)
             "NonTranslatable"
           elsif source.length >= 140 && source.match?(/\b(legal|privacy|terms|consent|retention|deletion)\b/i)
             "ApprovedOnly"
           else
             "AzureAllowed"
           end
  key = [source, context]
  entries[key] ||= {
    "id" => "application.copy.#{identity[0, 24]}",
    "source" => source,
    "context" => context,
    "sourceRevision" => Digest::SHA256.hexdigest(source)[0, 16],
    "placeholders" => source.scan(/\{([A-Za-z][A-Za-z0-9_]*)\}/).flatten.sort,
    "translationPolicy" => policy,
    "reuseScope" => "Global"
  }
end

Dir.glob(SWIFT_ROOT.join("**/*.swift")).sort.each do |path|
  source = File.read(path)
  source.scan(/LegendLocalized\(\s*(#{LITERAL})(?:\s*,\s*context:\s*(#{LITERAL}))?/) do |token, context_token|
    add.call(literal_value(token), context_token ? literal_value(context_token) : VISUAL)
  end
  source.to_enum(:scan, LITERAL).each do
    match = Regexp.last_match
    value = literal_value(match[0])
    next unless value&.match?(/\{[A-Za-z][A-Za-z0-9_]*\}/)
    window_start = [0, match.begin(0) - 700].max
    window = source[window_start, 1_400]
    add.call(value, window.include?("context: \"#{ACCESSIBILITY}\"") ? ACCESSIBILITY : VISUAL)
  end
end

Dir.glob(KOTLIN_ROOT.join("**/*.kt")).sort.each do |path|
  source = File.read(path)
  source.scan(/legendLocalized\(\s*(#{LITERAL})(?:\s*,\s*(#{LITERAL}))?/) do |token, context_token|
    add.call(literal_value(token), context_token ? literal_value(context_token) : VISUAL)
  end
  source.scan(/LegendPrimaryButton\(\s*(#{LITERAL})/) do |token|
    add.call(literal_value(token[0]), VISUAL)
  end
  source.scan(/LegendEmptyState\(\s*(#{LITERAL})\s*,\s*(#{LITERAL})/m) do |title, detail|
    add.call(literal_value(title), VISUAL)
    add.call(literal_value(detail), VISUAL)
  end
  %w[
    LegendJourneySectionLabel LegendMetric LegendSocialDetailField
    LegendCreatorInsightMetric LegendCreatorInsightValue
    AccountSettingsRow FinancialAvailabilityCard FinancialHeroMetric
    FinancialOutlookMetric
  ].each do |function|
    source.scan(/#{function}\(\s*(#{LITERAL})/) do |token|
      add.call(literal_value(token[0]), VISUAL)
    end
  end
  %w[
    LegendJourneyToggle JourneyChoiceSection LegendMessagingEmptyCard
    LegendCreatorInsightList
  ].each do |function|
    source.scan(/#{function}\(\s*(#{LITERAL})\s*,\s*(#{LITERAL})/m) do |first, second|
      add.call(literal_value(first), VISUAL)
      add.call(literal_value(second), VISUAL)
    end
  end
  # View models retain source-form failure copy so a language change can
  # re-render it through the active catalog instead of freezing the language
  # that happened to be active when the error occurred. Register only the
  # app-owned failure constructors/guards that can flow into those surfaces.
  source.each_line do |line|
    next unless line.match?(/LoadState\.Error|FounderAiStatusCard|\berror\(|\brequire\(/)

    line.scan(LITERAL).each do |token|
      value = literal_value(token)
      add.call(value, VISUAL) if value&.match?(/[[:space:].!?…]/)
    end
  end
end

# Server-owned native presentation projections are part of the application
# surface, but their stable keys and semantic codes are not. Register only the
# explicit human-copy positions so native renderers can resolve those values
# through the same catalog without translating arbitrary response data.
{
  ROOT.join("Infrastructure/Mobile/MobileFinancialPresentationEvaluator.cs") => [
    /\b(?:eyebrow|title|status|reason):\s*(#{LITERAL})/,
    /\b(?:AmountMetric|DateMetric|TextMetric)\(\s*(#{LITERAL})/
  ],
  ROOT.join("Infrastructure/Mobile/MobileFinancialHealthSnapshotProjection.cs") => [
    /\b(?:Title|Period):\s*(#{LITERAL})/,
    /\b(?:Currency|Percentage|Text|DualProtection)\(\s*#{LITERAL}\s*,\s*(#{LITERAL})/,
    /new MobileFinancialHealthGroup\(\s*#{LITERAL}\s*,\s*(#{LITERAL})/m
  ],
  ROOT.join("SHARED/Finance/LegendLivingBalanceSheetCalculator.cs") => [
    /\n\s*(#{LITERAL})\s*\n\s*\);/
  ]
}.each do |path, patterns|
  source = File.read(path)
  patterns.each do |pattern|
    source.scan(pattern) do |match|
      token = match.is_a?(Array) ? match[0] : match
      add.call(literal_value(token), VISUAL)
    end
  end
end

# Shared/server-owned copy uses a no-op source marker in C#. The marker keeps
# literals declared beside their authoritative workflow while this generator
# folds them into the same manifest consumed by both native applications.
Dir.glob([
  ROOT.join("Domain/**/*.cs").to_s,
  ROOT.join("Infrastructure/**/*.cs").to_s,
  ROOT.join("AgentPortal/Mobile/**/*.cs").to_s
]).sort.each do |path|
  source = File.read(path)
  source.scan(/ApplicationCopyText\.Source\(\s*(#{LITERAL})/) do |token|
    add.call(literal_value(token[0]), VISUAL)
  end
end

JSON.parse(File.read(DESIGN)).fetch("copy", {}).each_value do |source|
  add.call(source, VISUAL)
end

["LEGEND", "LEGEND®", "Legend", "Legend AI", "Legend® Ai", "OpenAI"].each do |brand|
  add.call(brand, VISUAL)
end

ordered_entries = entries.values.sort_by { |entry| [entry["context"], entry["source"]] }
catalog_identity = ordered_entries.map do |entry|
  [
    entry["id"],
    entry["source"],
    entry["context"],
    entry["sourceRevision"],
    entry["placeholders"].join(","),
    entry["translationPolicy"],
    entry["reuseScope"]
  ].join("\u001f")
end.join("\n")
catalog_version = "application-copy-v1-#{Digest::SHA256.hexdigest(catalog_identity)[0, 16]}"
manifest = {
  "catalogVersion" => catalog_version,
  "sourceLanguageCode" => "en",
  "entries" => ordered_entries
}
serialized_manifest = JSON.pretty_generate(manifest)
# Keep empty placeholder arrays in the repository's established multiline
# form across Ruby JSON versions so regeneration changes only copy content.
serialized_manifest.gsub!(
  '"placeholders": []',
  '"placeholders": [' + "\n\n" + '      ]'
)
File.write(MANIFEST, serialized_manifest + "\n")
puts "Generated #{manifest['entries'].length} canonical application-copy entries."

namespace Iiot.Protocol;

public static class TopicFilters
{
    public static bool IsValidTopicName(string topic)
    {
        return !string.IsNullOrWhiteSpace(topic)
            && !topic.Contains('\0')
            && !topic.Contains('+')
            && !topic.Contains('#');
    }

    public static bool IsValidFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Contains('\0'))
        {
            return false;
        }

        var levels = filter.Split('/');
        for (var index = 0; index < levels.Length; index++)
        {
            var level = levels[index];
            if (level.Contains('#') && (level != "#" || index != levels.Length - 1))
            {
                return false;
            }

            if (level.Contains('+') && level != "+")
            {
                return false;
            }
        }

        return true;
    }

    public static bool Matches(string filter, string topic)
    {
        if (!IsValidFilter(filter) || !IsValidTopicName(topic))
        {
            return false;
        }

        // MQTT reserves the $ namespace from wildcard subscriptions unless the filter opts in.
        if (topic.StartsWith('$') && !filter.StartsWith('$'))
        {
            return false;
        }

        var filters = filter.Split('/');
        var topics = topic.Split('/');
        for (var index = 0; index < filters.Length; index++)
        {
            if (filters[index] == "#")
            {
                return true;
            }

            if (index >= topics.Length || (filters[index] != "+" && filters[index] != topics[index]))
            {
                return false;
            }
        }

        return filters.Length == topics.Length;
    }
}

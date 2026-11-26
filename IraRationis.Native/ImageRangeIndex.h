// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>

namespace irx
{
    // Construct before publication; thereafter read-only. Preserve the union of
    // the supplied half-open ranges, including overlapping and adjacent ranges.
    template <typename Range, std::size_t Capacity>
    class ImageRangeIndex final
    {
    public:
        bool Build(const Range* input, std::size_t count) noexcept
        {
            if (!input || count == 0 || count > Capacity)
            {
                return false;
            }

            for (std::size_t i = 0; i < count; ++i)
            {
                if (input[i].start >= input[i].end)
                {
                    return false;
                }
            }

            std::copy_n(input, count, ranges_.begin());

            std::sort(ranges_.begin(), ranges_.begin() + count,
                [](const Range& left, const Range& right) noexcept
                {
                    return left.start < right.start;
                });

            std::size_t used = 0;

            for (std::size_t i = 0; i < count; ++i)
            {
                const auto range = ranges_[i];

                if (used != 0 && range.start <= ranges_[used - 1].end)
                {
                    ranges_[used - 1].end = (std::max)(ranges_[used - 1].end, range.end);
                }
                else
                {
                    ranges_[used++] = range;
                }
            }

            count_ = used;

            return true;
        }

        [[nodiscard]] bool Contains(std::uintptr_t address) const noexcept
        {
            std::size_t first = 0;
            auto last = count_;

            while (first < last)
            {
                const auto middle = first + (last - first) / 2;
                const auto& range = ranges_[middle];

                if (address < range.start)
                {
                    last = middle;
                }
                else if (address >= range.end)
                {
                    first = middle + 1;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

    private:
        std::array<Range, Capacity> ranges_{};
        std::size_t count_ = 0;
    };
}

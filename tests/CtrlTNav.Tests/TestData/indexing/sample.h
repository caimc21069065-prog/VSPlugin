// Sample C++ header used by the CtrlTNav indexing tests.
// It must stay readable by a human as well as by the tolerant extractor.
#pragma once

#include <cstdint>
#include <string>

#define NAV_SAMPLE_VERSION 3
#define NAV_SAMPLE_MAX(a, b) ((a) > (b) ? (a) : (b))
#define NAV_SAMPLE_API __declspec(dllexport)

namespace navsample
{
    // Object-like and function-like macros can appear anywhere.
#define NAV_INNER_MACRO(x) (x)

    enum class Color : std::uint8_t
    {
        Red = 1,
        Green,
        Blue = Red | Green,
    };

    enum LegacyMode
    {
        ModeOff = 0,
        ModeOn = 1
    };

    typedef unsigned long SampleSize;
    typedef void (*SampleCallback)(int value);
    using SampleAlias = std::string;

    template <typename T>
    using SampleVector = std::vector<T>;

    template <typename T>
    concept Numeric = requires(T value)
    {
        value + value;
    };

    struct Point
    {
        int X;
        int Y;
        static const int Dimensions = 2;

        int LengthSquared() const;
        static Point Origin();
        Point(int x, int y);
        ~Point();

        bool operator==(const Point& other) const;
        Point operator+(const Point& other) const;
    };

    class NAV_SAMPLE_API Widget : public Point
    {
    public:
        explicit Widget(int width);
        virtual ~Widget();

        void Draw(int x, int y);
        virtual int ComputeWidth() const = 0;

        static Widget* CreateDefault();

    protected:
        int Width;

    private:
        bool Visible;
        int LastWidth;
    };

    union Value
    {
        int Integer;
        float Real;
    };

    struct ForwardDeclared;

    typedef struct
    {
        int A;
        int B;
    } Pair;

    template <typename T>
    class Container
    {
    public:
        T Get(int index) const;
        void Set(int index, T value);

    private:
        T* items;
        int count;
    };

    extern "C"
    {
        int NAV_SAMPLE_API SampleAdd(int left, int right);
    }

    namespace detail
    {
        int InternalHelper(int value);

        namespace
        {
            int AnonymousHelper();
        }
    }

    extern SampleSize gTotalCount;
    static double gRatio;

    enum { AnonymousFirst = 1, AnonymousSecond = 2 };
}

int GlobalOutsideNamespace(int value);
static int gOutsideStatic;
typedef int GlobalIntAlias;

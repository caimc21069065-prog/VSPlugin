#include "sample.h"

#define CPP_FILE_MACRO(name) name

using namespace navsample;

namespace navsample
{
    int Point::LengthSquared() const
    {
        return X * X + Y * Y;
    }

    Point::Point(int x, int y)
        : X(x),
          Y(y)
    {
    }

    Point::~Point()
    {
    }

    Point Point::Origin()
    {
        return Point(0, 0);
    }

    bool Point::operator==(const Point& other) const
    {
        return X == other.X && Y == other.Y;
    }

    Point Point::operator+(const Point& other) const
    {
        return Point(X + other.X, Y + other.Y);
    }

    int SampleAdd(int left, int right)
    {
        int localVariable = left + right;
        return localVariable;
    }

    int detail::InternalHelper(int value)
    {
        return value * 2;
    }

    SampleSize gTotalCount = 0;

    int Widget::ComputeWidth() const
    {
        return Width;
    }
}

int GlobalOutsideNamespace(int value)
{
    static int localStatic = 0;
    return value + localStatic;
}

int gOutsideStatic = 5;

extern "C" int CLinkageFunction(int value)
{
    return value;
}

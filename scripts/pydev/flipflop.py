if request.IsInit:
    lastVal = 0

if request.IsRead:
    request.Value = lastVal
elif request.IsWrite:
    lastVal = request.Value

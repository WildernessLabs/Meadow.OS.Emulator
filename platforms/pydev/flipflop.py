if request.isInit:
    lastVal = 0

if request.isRead:
    request.value = lastVal
elif request.isWrite:
    lastVal = request.value
